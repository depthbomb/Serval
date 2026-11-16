#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Quic;
using System.Runtime.Versioning;
using Serval.Hosting;
using Serval.Http;
using Serval.Http.Constants;
using Serval.Transport.Http3.Protocol;

namespace Serval.Transport.Http3;

[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
internal sealed class Http3Connection
{
    private readonly QuicConnection                   _connection;
    private readonly ConcurrentDictionary<long, byte> _criticalStreams = new();
    private readonly IRequestHandler                  _handler;
    private readonly ILogger                          _logger;
    private readonly ServalOptions                    _options;
    private readonly SemaphoreSlim                    _requestSlots;
    private readonly ConcurrentDictionary<long, Task> _streams = new();

    public Http3Connection(QuicConnection  connection,
                           ServalOptions   options,
                           IRequestHandler handler,
                           SemaphoreSlim   requestSlots,
                           ILogger         logger)
    {
        _connection   = connection;
        _options      = options;
        _handler      = handler;
        _requestSlots = requestSlots;
        _logger       = logger;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var       lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        await using var control  = await _connection.OpenOutboundStreamAsync(QuicStreamType.Unidirectional, lifetime.Token);

        await control.WriteAsync(Frames.Integer(0), lifetime.Token);

        // Advertise no QPACK dynamic table or blocked streams, matching the static-only decoder below.
        var settings = Frames.Integer(1).Concat(Frames.Integer(0)).Concat(Frames.Integer(6)).Concat(Frames.Integer(_options.MaxRequestHeaderBytes)).Concat(Frames.Integer(7)).Concat(Frames.Integer(0)).ToArray();
        await Frames.WriteAsync(control, Frames.Settings, settings, lifetime.Token);
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                var stream     = await _connection.AcceptInboundStreamAsync(lifetime.Token);
                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _streams[stream.Id] = completion.Task;
                _                   = ProcessStreamAsync(stream, completion, lifetime.Token);
            }
        }
        catch (QuicException exception)
        {
            _logger.LogDebug(exception, "HTTP/3 peer closed the connection.");
        }
        catch (OperationCanceledException exception) when (lifetime.IsCancellationRequested)
        {
            _logger.LogDebug(exception, "The HTTP/3 connection stopped.");
        }
        finally
        {
            await lifetime.CancelAsync();
            await Task.WhenAll(_streams.Values);
            if (cancellationToken.IsCancellationRequested)
            {
                await _connection.CloseAsync(Http3Error.NoError, CancellationToken.None);
            }
        }
    }

    private async Task ProcessStreamAsync(QuicStream stream, TaskCompletionSource completion, CancellationToken cancellationToken)
    {
        await using (stream)
        {
            try
            {
                if (stream.Type == QuicStreamType.Unidirectional)
                {
                    await ProcessUnidirectionalAsync(stream, cancellationToken);
                }
                else if (await _requestSlots.WaitAsync(0, cancellationToken))
                {
                    try
                    {
                        await ProcessRequestAsync(stream, cancellationToken);
                    }
                    finally
                    {
                        _requestSlots.Release();
                    }
                }
                else
                {
                    stream.Abort(QuicAbortDirection.Both, 0x10b);
                }
            }
            catch (Http3ProtocolException exception) when (exception.ErrorCode == Http3Error.Message && stream.Type == QuicStreamType.Bidirectional)
            {
                // A malformed request must not let one stream terminate unrelated requests on the connection.
                stream.Abort(QuicAbortDirection.Both, exception.ErrorCode);
            }
            catch (Http3ProtocolException exception)
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug(exception, "HTTP/3 protocol error {ErrorCode}.", exception.ErrorCode);
                }

                await _connection.CloseAsync(exception.ErrorCode, CancellationToken.None);
            }
            catch (Exception exception) when (exception is QuicException or OperationCanceledException or EndOfStreamException)
            {
                _logger.LogDebug(exception, "HTTP/3 stream closed.");
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "HTTP/3 stream failed.");
                stream.Abort(QuicAbortDirection.Both, Http3Error.GeneralProtocol);
            }
            finally
            {
                _streams.TryRemove(stream.Id, out _);
                completion.TrySetResult();
            }
        }
    }

    private async Task ProcessRequestAsync(QuicStream stream, CancellationToken cancellationToken)
    {
        using var headerTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        headerTimeout.CancelAfter(_options.RequestTimeout);

        List<(string Name, string Value)> fields;
        while (true)
        {
            var frame = await Frames.ReadAsync(stream, headerTimeout.Token) ?? throw new Http3ProtocolException(Http3Error.Message, "Missing request headers.");
            if (frame.Type == Frames.Headers)
            {
                fields = QPack.Decode(await Frames.ReadPayloadAsync(stream, frame.Length, _options.MaxRequestHeaderBytes, headerTimeout.Token), _options.MaxRequestHeaderBytes);
                break;
            }

            Http3BodyStream.RejectForbiddenFrame(frame.Type);
            if (frame.Type == Frames.Data)
            {
                throw new Http3ProtocolException(Http3Error.FrameUnexpected, "DATA before HEADERS.");
            }

            await Frames.SkipAsync(stream, frame.Length, headerTimeout.Token);
        }

        var head = RequestHeaders.Parse(fields);
        var body = new Http3BodyStream(stream, _options.MaxRequestBodyBytes, _options.MaxRequestHeaderBytes, head.ContentLength);
        var request = new Request(head.Method, head.Url, HttpVersion.Version30, head.Headers, body, _options.MaxRequestBodyBytes,
            new RequestConnection(_connection.RemoteEndPoint, _connection.LocalEndPoint));
        await _handler.HandleAsync(request, new Http3Response(stream, head.Method == HttpMethods.Head), cancellationToken);
    }

    private async Task ProcessUnidirectionalAsync(QuicStream stream, CancellationToken cancellationToken)
    {
        var type = await Frames.ReadIntegerAsync(stream, cancellationToken);
        if (type is 0 or 2 or 3)
        {
            if (!_criticalStreams.TryAdd(type.Value, 0))
            {
                throw new Http3ProtocolException(Http3Error.StreamCreation, "Duplicate critical stream.");
            }
        }

        switch (type)
        {
            case 0:
            {
                var first = await Frames.ReadAsync(stream, cancellationToken);
                if (first?.Type != Frames.Settings)
                {
                    throw new Http3ProtocolException(Http3Error.MissingSettings, "SETTINGS must be the first control frame.");
                }

                var       payload = await Frames.ReadPayloadAsync(stream, first.Value.Length, _options.MaxRequestHeaderBytes, cancellationToken);
                using var values  = new MemoryStream(payload);
                var       seen    = new HashSet<long>();
                while (await Frames.ReadIntegerAsync(values, cancellationToken) is {} setting)
                {
                    _ = await Frames.ReadIntegerAsync(values, cancellationToken) ?? throw new Http3ProtocolException(Http3Error.Settings, "Missing setting value.");
                    if (!seen.Add(setting) || setting is 2 or 3 or 4 or 5)
                    {
                        throw new Http3ProtocolException(Http3Error.Settings, "Invalid or duplicate setting.");
                    }
                }

                while (await Frames.ReadAsync(stream, cancellationToken) is {} frame)
                {
                    if (frame.Type is Frames.Settings or Frames.Data or Frames.Headers or 2 or 5 or 6 or 8 or 9)
                    {
                        throw new Http3ProtocolException(Http3Error.FrameUnexpected, "Forbidden frame on the control stream.");
                    }

                    await Frames.SkipAsync(stream, frame.Length, cancellationToken);
                }
            }
                break;
            case 2 or 3:
            {
                var buffer = new byte[1];
                while (await stream.ReadAsync(buffer, cancellationToken) > 0)
                {
                    if (type == 3 || buffer[0] != 0x20)
                    {
                        throw new Http3ProtocolException(type == 2 ? Http3Error.QpackEncoder : Http3Error.QpackDecoder, "Unexpected instruction with zero dynamic table capacity.");
                    }
                }
            }
                break;
            case 1:
            {
                throw new Http3ProtocolException(Http3Error.StreamCreation, "Clients cannot create push streams.");
            }
            default:
            {
                // Unknown stream types are extensions. Drain them without treating their EOF as a critical-stream failure.
                await stream.CopyToAsync(Stream.Null, cancellationToken);
                return;
            }
        }

        throw new Http3ProtocolException(Http3Error.ClosedCriticalStream, "A critical stream was closed.");
    }
}
