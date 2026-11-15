#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.Extensions.Logging;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using Serval.Hosting;
using Serval.Http;
using Serval.Transport.Http2.Protocol;

namespace Serval.Transport.Http2;

internal sealed partial class Http2Connection
{
    private          int                                           _connectionReceiveWindow = Frames.InitialWindow;
    private          int                                           _lastAcceptedStreamId;
    private          int                                           _lastSeenStreamId;
    private          long                                          _maximumResponseHeaders = long.MaxValue;
    private          bool                                          _peerGoingAway;
    private          int                                           _pendingConnectionCredit;
    private          PendingHeaders?                               _pendingHeaders;
    private          bool                                          _settingsAcknowledged;
    private readonly CancellationTokenSource                       _closed;
    private readonly RequestConnection                             _connection;
    private readonly HPack                                         _decoder = new();
    private readonly IRequestHandler                               _handler;
    private readonly Http2Options                                  _http2Options;
    private readonly ILogger                                       _logger;
    private readonly ServalOptions                                 _options;
    private readonly FrameReader                                   _reader;
    private readonly Lock                                          _receiveGate = new();
    private readonly SemaphoreSlim                                 _requestSlots;
    private readonly ConcurrentDictionary<int, Http2RequestStream> _requests   = new();
    private readonly Queue<long>                                   _resetTimes = new();
    private readonly ConcurrentDictionary<int, Task>               _running    = new();
    private readonly SendFlowControl                               _sendFlow   = new();
    private readonly Stream                                        _transport;
    private readonly FrameWriter                                   _writer;

    public Http2Connection(Stream             transport,
                           Http2Options       http2Options,
                           ServalOptions      options,
                           IRequestHandler    handler,
                           SemaphoreSlim      requestSlots,
                           ILogger            logger,
                           CancellationToken  cancellationToken,
                           RequestConnection? connection = null)
    {
        _transport    = transport;
        _connection   = connection ?? new RequestConnection();
        _http2Options = http2Options;
        _options      = options;
        _handler      = handler;
        _requestSlots = requestSlots;
        _logger       = logger;
        _closed       = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _writer       = new FrameWriter(transport, _closed, http2Options.IdleTimeout);
        _reader       = new FrameReader(transport);
    }

    public async Task RunAsync()
    {
        try
        {
            await InitializeAsync();

            using var idle = CancellationTokenSource.CreateLinkedTokenSource(_closed.Token);
            while (!_closed.IsCancellationRequested)
            {
                var readTimeout = _http2Options.IdleTimeout;
                if (_pendingHeaders is { } pending && _options.RequestTimeout != Timeout.InfiniteTimeSpan)
                {
                    var remaining = _options.RequestTimeout - Stopwatch.GetElapsedTime(pending.Started);
                    if (remaining <= TimeSpan.Zero)
                    {
                        throw new Http2ProtocolException(Http2Error.EnhanceYourCalm, "The request header block timed out.");
                    }

                    readTimeout = remaining < readTimeout ? remaining : readTimeout;
                }

                idle.CancelAfter(readTimeout);
                var frame = await _reader.ReadAsync(idle.Token);
                idle.CancelAfter(Timeout.InfiniteTimeSpan);
                if (frame is null)
                {
                    break;
                }

                try
                {
                    var interleaved = _pendingHeaders is not null && (frame.Value.Type != Frames.Continuation || frame.Value.StreamId != _pendingHeaders.StreamId);
                    if (interleaved)
                    {
                        throw new Http2ProtocolException(Http2Error.Protocol, "A header block cannot be interleaved with another frame.");
                    }

                    await ProcessFrameAsync(frame.Value);
                }
                catch (Http2ProtocolException exception) when (exception.StreamId > 0)
                {
                    await ResetStreamAsync(exception.StreamId, exception.ErrorCode);
                }
            }
        }
        catch (Http2ProtocolException exception)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(exception, "HTTP/2 connection protocol error {ErrorCode}.", exception.ErrorCode);
            }

            await TryGoAwayAsync(exception.ErrorCode);
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException)
        {
            _logger.LogDebug(exception, "HTTP/2 connection closed.");
        }
        finally
        {
            _sendFlow.Close();
            await _closed.CancelAsync();
            foreach (var request in _requests.Values)
            {
                await request.ResetAsync();
            }

            await Task.WhenAll(_running.Values);
            foreach (var request in _requests.Values)
            {
                request.Dispose();
            }

            _requests.Clear();
            if (_pendingHeaders is { } pendingHeaders)
            {
                await pendingHeaders.Block.DisposeAsync();
            }

            await _writer.DisposeAsync();
            _closed.Dispose();
        }
    }

    private async ValueTask InitializeAsync()
    {
        using var handshake = CancellationTokenSource.CreateLinkedTokenSource(_closed.Token);
        handshake.CancelAfter(_http2Options.HandshakeTimeout);
        var preface = new byte[24];
        await _transport.ReadExactlyAsync(preface, handshake.Token);
        if (!preface.AsSpan().SequenceEqual("PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n"u8))
        {
            throw new Http2ProtocolException(Http2Error.Protocol, "Invalid HTTP/2 connection preface.");
        }

        var settings = new byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(settings, 3);
        BinaryPrimitives.WriteUInt32BigEndian(settings.AsSpan(2), (uint)_options.MaxConcurrentRequests);
        BinaryPrimitives.WriteUInt16BigEndian(settings.AsSpan(6), 6);
        BinaryPrimitives.WriteUInt32BigEndian(settings.AsSpan(8), (uint)_options.MaxRequestHeaderBytes);
        await _writer.WriteAsync(Frames.Settings, 0, 0, settings, handshake.Token);
        var first = await _reader.ReadAsync(handshake.Token);
        if (first is not { Type: Frames.Settings } || (first.Value.Flags & Frames.Ack) != 0)
        {
            throw new Http2ProtocolException(Http2Error.Protocol, "SETTINGS must be the first client frame.");
        }

        await ProcessSettingsAsync(first.Value);
    }

    private ValueTask ProcessFrameAsync(Frame frame) => frame.Type switch
    {
        Frames.Data         => ProcessDataAsync(frame),
        Frames.Headers      => ProcessHeadersAsync(frame),
        Frames.Continuation => ProcessContinuationAsync(frame),
        Frames.Settings     => ProcessSettingsAsync(frame),
        Frames.Ping         => ProcessPingAsync(frame),
        Frames.WindowUpdate => ProcessWindowUpdateAsync(frame),
        Frames.ResetStream  => ProcessResetAsync(frame),
        Frames.Priority     => ProcessPriorityAsync(frame),
        Frames.GoAway       => ProcessGoAwayAsync(frame),
        Frames.PushPromise  => ValueTask.FromException(new Http2ProtocolException(Http2Error.Protocol, "Clients cannot send PUSH_PROMISE.")),
        _                   => ValueTask.CompletedTask,
    };

    private async ValueTask TryGoAwayAsync(uint errorCode)
    {
        if (_closed.IsCancellationRequested)
        {
            return;
        }

        try
        {
            await _writer.WriteGoAwayAsync(_lastAcceptedStreamId, errorCode, _closed.Token);
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException)
        {
            _logger.LogDebug(exception, "The HTTP/2 peer disconnected before GOAWAY was sent.");
        }
    }

    private sealed record PendingHeaders(int StreamId, bool EndStream, bool SelfDependency, bool IsNewStream, long Started, MemoryStream Block);
}
