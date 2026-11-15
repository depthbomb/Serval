#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.Extensions.Logging;
using System.Buffers.Binary;
using System.Diagnostics;
using Serval.Transport.Http2.Protocol;

namespace Serval.Transport.Http2;

internal sealed partial class Http2Connection
{
    private async ValueTask ProcessSettingsAsync(Frame frame)
    {
        Frames.RequireConnection(frame);
        if ((frame.Flags & Frames.Ack) != 0)
        {
            Frames.RequireLength(frame, 0);
            if (_settingsAcknowledged)
            {
                throw new Http2ProtocolException(Http2Error.Protocol, "Unexpected SETTINGS acknowledgement.");
            }

            _settingsAcknowledged = true;
            return;
        }

        if (frame.Payload.Length % 6 != 0)
        {
            throw new Http2ProtocolException(Http2Error.FrameSize, "Invalid SETTINGS length.");
        }

        for (var offset = 0; offset < frame.Payload.Length; offset += 6)
        {
            var setting = BinaryPrimitives.ReadUInt16BigEndian(frame.Payload.Span[offset..]);
            var value   = BinaryPrimitives.ReadUInt32BigEndian(frame.Payload.Span[(offset + 2)..]);
            switch (setting)
            {
                case 2 when value > 1:
                    throw new Http2ProtocolException(Http2Error.Protocol, "Invalid ENABLE_PUSH setting.");
                case 4:
                    if (value > int.MaxValue)
                    {
                        throw new Http2ProtocolException(Http2Error.FlowControl, "Invalid INITIAL_WINDOW_SIZE.");
                    }

                    _sendFlow.UpdateInitialWindow((int)value);
                    break;
                case 5 when value is < Frames.MaximumPayload or > 16777215:
                    throw new Http2ProtocolException(Http2Error.Protocol, "Invalid MAX_FRAME_SIZE.");
                case 6:
                    Interlocked.Exchange(ref _maximumResponseHeaders, value);
                    break;
            }
        }

        await _writer.QueueControlAsync(Frames.Settings, Frames.Ack, 0, ReadOnlyMemory<byte>.Empty);
    }

    private ValueTask ProcessPingAsync(Frame frame)
    {
        Frames.RequireConnection(frame);
        Frames.RequireLength(frame, 8);
        return (frame.Flags & Frames.Ack) == 0 ? _writer.QueueControlAsync(Frames.Ping, Frames.Ack, 0, frame.Payload) : ValueTask.CompletedTask;
    }

    private ValueTask ProcessWindowUpdateAsync(Frame frame)
    {
        Frames.RequireLength(frame, 4);
        if (frame.StreamId > _lastSeenStreamId)
        {
            throw new Http2ProtocolException(Http2Error.Protocol, "WINDOW_UPDATE on an idle stream.");
        }

        var increment = BinaryPrimitives.ReadInt32BigEndian(frame.Payload.Span) & int.MaxValue;
        _sendFlow.Update(frame.StreamId, increment);
        return ValueTask.CompletedTask;
    }

    private async ValueTask ProcessResetAsync(Frame frame)
    {
        Frames.RequireStream(frame);
        Frames.RequireLength(frame, 4);
        if (frame.StreamId > _lastSeenStreamId)
        {
            throw new Http2ProtocolException(Http2Error.Protocol, "RST_STREAM on an idle stream.");
        }

        if (!await AllowResetAsync())
        {
            return;
        }

        // The dictionary is thread-safe; _receiveGate protects stream state transitions.
        // ReSharper disable once InconsistentlySynchronizedField
        if (_requests.TryGetValue(frame.StreamId, out var request))
        {
            await request.ResetAsync();
            _sendFlow.Remove(frame.StreamId);
            TryRemoveRequest(request);
        }
    }

    private ValueTask ProcessGoAwayAsync(Frame frame)
    {
        Frames.RequireConnection(frame);
        if (frame.Payload.Length < 8)
        {
            throw new Http2ProtocolException(Http2Error.FrameSize, "Invalid GOAWAY length.");
        }

        _peerGoingAway = true;
        return ValueTask.CompletedTask;
    }

    private async ValueTask ResetStreamAsync(int streamId, uint errorCode)
    {
        // The dictionary is thread-safe; _receiveGate protects stream state transitions.
        // ReSharper disable once InconsistentlySynchronizedField
        if (_requests.TryGetValue(streamId, out var request))
        {
            if (!await request.ResetAsync())
            {
                return;
            }

            _sendFlow.Remove(streamId);
            TryRemoveRequest(request);
        }

        if (!_closed.IsCancellationRequested && (errorCode == Http2Error.NoError || await AllowResetAsync()))
        {
            try
            {
                await _writer.QueueResetAsync(streamId, errorCode);
            }
            catch (Exception exception) when (exception is IOException or OperationCanceledException)
            {
                _logger.LogDebug(exception, "The HTTP/2 peer closed before RST_STREAM was sent.");
                await _closed.CancelAsync();
            }
        }
    }

    private async ValueTask<bool> AllowResetAsync()
    {
        // Bound reset churn in both directions: Rapid Reset and induced server resets can bypass stream concurrency limits.
        lock (_receiveGate)
        {
            var now = Stopwatch.GetTimestamp();
            while (_resetTimes.TryPeek(out var started) && Stopwatch.GetElapsedTime(started, now) >= TimeSpan.FromMinutes(1))
            {
                _resetTimes.Dequeue();
            }

            if (_resetTimes.Count < _http2Options.MaxStreamResetsPerMinute)
            {
                _resetTimes.Enqueue(now);
                return true;
            }
        }

        await TryGoAwayAsync(Http2Error.EnhanceYourCalm);
        await _closed.CancelAsync();
        return false;
    }

    private async ValueTask ReturnReceiveCreditAsync(int streamId, int count)
    {
        if (count == 0 || _closed.IsCancellationRequested)
        {
            return;
        }

        int increment;
        lock (_receiveGate)
        {
            if (!_requests.TryGetValue(streamId, out var request) || request.RemoteEnded || request.IsReset)
            {
                return;
            }

            request.PendingReceiveCredit += count;
            if (request.PendingReceiveCredit < Frames.InitialWindow / 2)
            {
                return;
            }

            increment                    =  request.PendingReceiveCredit;
            request.PendingReceiveCredit =  0;
            request.ReceiveWindow        += increment;
        }

        // Consumed bytes need their credit returned even if the body reader has since canceled its read.
        await _writer.QueueWindowUpdateAsync(streamId, increment);
    }

    private void TryRemoveRequest(Http2RequestStream request)
    {
        lock (_receiveGate)
        {
            // Both halves must be done before disposing the body and token that application code may still use.
            if (!request.HandlerCompleted || request is { RemoteEnded: false, IsReset: false } || !_requests.TryRemove(request.Id, out _))
            {
                return;
            }

            _sendFlow.Remove(request.Id);
            request.Dispose();
        }
    }

    private static ValueTask ProcessPriorityAsync(Frame frame)
    {
        Frames.RequireStream(frame);
        Frames.RequireLength(frame, 5);
        return (BinaryPrimitives.ReadInt32BigEndian(frame.Payload.Span) & int.MaxValue) == frame.StreamId
            ? throw new Http2ProtocolException(Http2Error.Protocol, "A stream cannot depend on itself.", frame.StreamId)
            : ValueTask.CompletedTask;
    }
}
