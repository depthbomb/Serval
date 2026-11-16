#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.Extensions.Logging;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using Serval.Http;
using Serval.Http.Constants;
using Serval.Transport.Http2.Protocol;

namespace Serval.Transport.Http2;

internal sealed partial class Http2Connection
{
    public ValueTask WriteHeadersAsync(Http2RequestStream request,
                                       int                status,
                                       Headers            headers,
                                       bool               endStream,
                                       CancellationToken  cancellationToken)
    {
        var block = HPack.Encode(status, headers, Interlocked.Read(ref _maximumResponseHeaders), request.Id);
        return _writer.WriteHeadersAsync(request.Id, block, endStream, cancellationToken);
    }

    public async ValueTask WriteDataAsync(Http2RequestStream request, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        while (data.Length > 0)
        {
            var count = await _sendFlow.ReserveAsync(request.Id, Math.Min(data.Length, Frames.MaximumPayload), cancellationToken);
            try
            {
                await _writer.WriteAsync(Frames.Data, 0, request.Id, data[..count], cancellationToken);
            }
            catch
            {
                _sendFlow.Refund(request.Id, count);
                throw;
            }

            data = data[count..];
        }
    }

    public ValueTask EndResponseAsync(Http2RequestStream request, CancellationToken cancellationToken)
        => _writer.WriteAsync(Frames.Data, Frames.EndStream, request.Id, ReadOnlyMemory<byte>.Empty, cancellationToken);

    public ValueTask AbortResponseAsync(Http2RequestStream request) => ResetStreamAsync(request.Id, Http2Error.Cancel);

    private async ValueTask ProcessHeadersAsync(Frame frame)
    {
        Frames.RequireStream(frame);
        if ((frame.StreamId & 1) == 0)
        {
            throw new Http2ProtocolException(Http2Error.Protocol, "Client request stream IDs must be odd.");
        }

        var payload        = RemovePadding(frame);
        var selfDependency = false;
        if ((frame.Flags & Frames.HasPriority) != 0)
        {
            if (payload.Length < 5)
            {
                throw new Http2ProtocolException(Http2Error.FrameSize, "HEADERS priority information is truncated.");
            }

            selfDependency = (BinaryPrimitives.ReadInt32BigEndian(payload.Span) & int.MaxValue) == frame.StreamId;
            payload        = payload[5..];
        }

        var block = new MemoryStream();
        var isNew = frame.StreamId > _lastSeenStreamId;
        if (isNew)
        {
            _lastSeenStreamId = frame.StreamId;
        }

        _pendingHeaders = new PendingHeaders(frame.StreamId, (frame.Flags & Frames.EndStream)  != 0, selfDependency, isNew, Stopwatch.GetTimestamp(), block);
        await AppendHeaderBlockAsync(payload, (frame.Flags                & Frames.EndHeaders) != 0);
    }

    private ValueTask ProcessContinuationAsync(Frame frame)
    {
        if (_pendingHeaders is null || _pendingHeaders.StreamId != frame.StreamId)
        {
            throw new Http2ProtocolException(Http2Error.Protocol, "Unexpected CONTINUATION frame.");
        }

        return AppendHeaderBlockAsync(frame.Payload, (frame.Flags & Frames.EndHeaders) != 0);
    }

    private async ValueTask AppendHeaderBlockAsync(ReadOnlyMemory<byte> fragment, bool complete)
    {
        var pending = _pendingHeaders!;
        if (pending.Block.Length + fragment.Length > _options.MaxRequestHeaderBytes)
        {
            throw new Http2ProtocolException(Http2Error.EnhanceYourCalm, "The compressed header block is too large.");
        }

        await pending.Block.WriteAsync(fragment, _closed.Token);
        if (!complete)
        {
            return;
        }

        _pendingHeaders = null;

        using (pending.Block)
        {
            // Decode even a stream we'll reject: HPACK's dynamic table belongs to the whole connection.
            var fields = _decoder.Decode(pending.Block.GetBuffer().AsSpan(0, (int)pending.Block.Length), _options.MaxRequestHeaderBytes);
            if (pending.SelfDependency)
            {
                throw new Http2ProtocolException(Http2Error.Protocol, "A stream cannot depend on itself.", pending.StreamId);
            }

            if (_requests.TryGetValue(pending.StreamId, out var existing))
            {
                if (existing.RemoteEnded || existing.IsReset)
                {
                    throw new Http2ProtocolException(Http2Error.StreamClosed, "HEADERS on a closed request stream.", pending.StreamId);
                }

                if (!pending.EndStream)
                {
                    throw new Http2ProtocolException(Http2Error.Protocol, "Trailers must end the request stream.", pending.StreamId);
                }

                ValidateTrailers(fields, pending.StreamId);
                EndInput(existing);
                return;
            }

            if (!pending.IsNewStream)
            {
                throw new Http2ProtocolException(Http2Error.StreamClosed, "The request stream has already closed.", pending.StreamId);
            }

            var head = RequestHeaders.Parse(fields, pending.StreamId, _http2Options.UseTls);
            if (_peerGoingAway || _requests.Count >= _options.MaxConcurrentRequests || !await _requestSlots.WaitAsync(0, _closed.Token))
            {
                await ResetStreamAsync(pending.StreamId, Http2Error.RefusedStream);
                return;
            }

            var request = new Http2RequestStream(pending.StreamId, head, (count, _) => ReturnReceiveCreditAsync(pending.StreamId, count), _closed.Token);
            _requests[request.Id] = request;
            _sendFlow.Open(request.Id);
            _lastAcceptedStreamId = request.Id;
            if (pending.EndStream)
            {
                try
                {
                    EndInput(request);
                }
                catch
                {
                    request.HandlerCompleted = true;
                    await request.ResetAsync();
                    TryRemoveRequest(request);
                    _requestSlots.Release();
                    throw;
                }
            }

            _running[request.Id] = request.Completion.Task;
            _                    = Task.Run(() => ProcessRequestAsync(request));
        }
    }

    private async ValueTask ProcessDataAsync(Frame frame)
    {
        Frames.RequireStream(frame);
        if (frame.StreamId > _lastSeenStreamId)
        {
            throw new Http2ProtocolException(Http2Error.Protocol, "DATA on an idle stream.");
        }

        lock (_receiveGate)
        {
            _connectionReceiveWindow -= frame.Payload.Length;
            if (_connectionReceiveWindow < 0)
            {
                throw new Http2ProtocolException(Http2Error.FlowControl, "The connection receive window was exceeded.");
            }
        }

        try
        {
            if (!_requests.TryGetValue(frame.StreamId, out var request) || request.IsReset)
            {
                // Frames already in flight after a reset still consume connection-level credit.
                return;
            }

            if (request.RemoteEnded)
            {
                throw new Http2ProtocolException(Http2Error.StreamClosed, "DATA after END_STREAM.", frame.StreamId);
            }

            lock (_receiveGate)
            {
                request.ReceiveWindow -= frame.Payload.Length;
                if (request.ReceiveWindow < 0)
                {
                    throw new Http2ProtocolException(Http2Error.FlowControl, "The stream receive window was exceeded.", frame.StreamId);
                }
            }

            var data = RemovePadding(frame);
            request.ReceivedBytes += data.Length;
            if (request.Head.ContentLength is {} expected && request.ReceivedBytes > expected)
            {
                throw new Http2ProtocolException(Http2Error.Protocol, "DATA exceeds Content-Length.", frame.StreamId);
            }

            if (request.ReceivedBytes > _options.MaxRequestBodyBytes)
            {
                request.Body.Complete(new HttpErrorException(StatusCodes.PayloadTooLarge));
                var discarded = request.Body.Discard();
                await ReturnReceiveCreditAsync(request.Id, discarded);
            }

            var buffered = request.Body.Append(data.Span);
            // Padding consumes flow-control credit too, but never waits for the application to read it.
            var consumed = frame.Payload.Length - (buffered ? data.Length : 0);
            await ReturnReceiveCreditAsync(request.Id, consumed);
            if ((frame.Flags & Frames.EndStream) != 0)
            {
                EndInput(request);
            }
        }
        finally
        {
            // Return connection credit as frames arrive. Each stream's credit still bounds its unread body,
            // so one slow upload doesn't prevent unrelated streams from receiving data.
            var increment = 0;
            lock (_receiveGate)
            {
                // Small credit updates can make concurrent uploads fragment into
                // tiny DATA frames. Replenish half a window at a time instead.
                _pendingConnectionCredit += frame.Payload.Length;
                if (_pendingConnectionCredit >= Frames.InitialWindow / 2)
                {
                    increment                =  _pendingConnectionCredit;
                    _pendingConnectionCredit =  0;
                    _connectionReceiveWindow += increment;
                }
            }

            if (increment > 0 && !_closed.IsCancellationRequested)
            {
                await _writer.QueueWindowUpdateAsync(0, increment);
            }
        }
    }

    private void EndInput(Http2RequestStream request)
    {
        if (request.Head.ContentLength is {} expected && request.ReceivedBytes != expected)
        {
            throw new Http2ProtocolException(Http2Error.Protocol, "Content-Length does not match the request body.", request.Id);
        }

        lock (_receiveGate)
        {
            request.RemoteEnded = true;
        }

        request.Body.Complete();
        TryRemoveRequest(request);
    }

    private async Task ProcessRequestAsync(Http2RequestStream request)
    {
        try
        {
            var message = new Request(request.Head.Method, request.Head.Url, HttpVersion.Version20, request.Head.Headers, request.Body, _options.MaxRequestBodyBytes, _connection);
            await _handler.HandleAsync(message, new Http2Response(this, request), request.Token);
        }
        catch (Http2ProtocolException exception)
        {
            await ResetStreamAsync(request.Id, exception.ErrorCode);
        }
        catch (Exception exception)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(exception, "HTTP/2 request stream {StreamId} ended.", request.Id);
            }

            await ResetStreamAsync(request.Id, Http2Error.Internal);
        }
        finally
        {
            try
            {
                var discarded = request.Body.Discard();
                await ReturnReceiveCreditAsync(request.Id, discarded);
            }
            catch (Exception exception) when (exception is IOException or OperationCanceledException)
            {
                _logger.LogDebug(exception, "The HTTP/2 peer closed before unused body credit was returned.");
            }

            lock (_receiveGate)
            {
                request.HandlerCompleted = true;
            }

            // Stop unread uploads once the response is complete so half-open streams cannot retain server state indefinitely.
            if (request is { RemoteEnded: false, IsReset: false } && !_closed.IsCancellationRequested)
            {
                await ResetStreamAsync(request.Id, Http2Error.NoError);
            }

            TryRemoveRequest(request);
            _requestSlots.Release();
            _running.TryRemove(request.Id, out _);
            request.Completion.TrySetResult();
        }
    }

    private static ReadOnlyMemory<byte> RemovePadding(Frame frame)
    {
        if ((frame.Flags & Frames.Padded) == 0)
        {
            return frame.Payload;
        }

        if (frame.Payload.Length == 0 || frame.Payload.Span[0] >= frame.Payload.Length)
        {
            throw new Http2ProtocolException(Http2Error.Protocol, "Invalid frame padding.");
        }

        return frame.Payload.Slice(1, frame.Payload.Length - 1 - frame.Payload.Span[0]);
    }

    private static void ValidateTrailers(List<(string Name, string Value)> fields, int streamId)
    {
        foreach (var (name, value) in fields)
        {
            try
            {
                Headers.ValidateTrailer(name, value);
            }
            catch (ArgumentException)
            {
                throw new Http2ProtocolException(Http2Error.Protocol, "Invalid trailer field.", streamId);
            }
        }
    }
}
