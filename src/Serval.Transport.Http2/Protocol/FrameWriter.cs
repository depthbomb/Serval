#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using System.Buffers.Binary;
using System.Threading.Channels;

namespace Serval.Transport.Http2.Protocol;

internal sealed class FrameWriter : IAsyncDisposable
{
    private          int                     _buffered;
    private readonly SemaphoreSlim           _applicationSlots = new(256);
    private readonly List<WriteRequest>      _batch            = new(64);
    private readonly byte[]                  _buffer           = new byte[64 * 1024];
    private readonly CancellationTokenSource _connectionClosed;
    private readonly SemaphoreSlim           _controlSlots = new(256);
    private readonly CancellationTokenSource _deadline;
    private readonly Task                    _pump;
    private readonly Channel<WriteRequest> _requests = Channel.CreateUnbounded<WriteRequest>(new UnboundedChannelOptions
    {
        SingleReader                  = true,
        // An idle writer can start on the caller's thread without a scheduler
        // hop. Socket I/O still uses WriteAsync, and response completion
        // continuations stay asynchronous to avoid reentrant application code.
        AllowSynchronousContinuations = true,
    });
    private readonly Stream   _stream;
    private readonly TimeSpan _writeTimeout;

    public FrameWriter(Stream stream, CancellationTokenSource connectionClosed, TimeSpan writeTimeout)
    {
        _stream           = stream;
        _connectionClosed = connectionClosed;
        _deadline         = CancellationTokenSource.CreateLinkedTokenSource(connectionClosed.Token);
        _writeTimeout     = writeTimeout;
        _pump             = RunAsync();
    }

    public async ValueTask DisposeAsync()
    {
        _requests.Writer.TryComplete();

        await _pump;

        _deadline.Dispose();
    }

    public ValueTask WriteAsync(byte type, byte flags, int streamId, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        if (payload.Length > Frames.MaximumPayload)
        {
            throw new ArgumentOutOfRangeException(nameof(payload));
        }

        return EnqueueAsync(new WriteRequest(type, flags, streamId, payload, false, cancellationToken));
    }

    public ValueTask WriteHeadersAsync(int                  streamId,
                                       ReadOnlyMemory<byte> block,
                                       bool                 endStream,
                                       CancellationToken    cancellationToken)
        => EnqueueAsync(new WriteRequest(Frames.Headers, endStream ? Frames.EndStream : (byte)0, streamId, block, true, cancellationToken));

    public async ValueTask QueueControlAsync(byte type, byte flags, int streamId, ReadOnlyMemory<byte> payload)
    {
        if (payload.Length > 8)
        {
            throw new ArgumentOutOfRangeException(nameof(payload));
        }

        // Reserve independent space for control frames so application writes
        // cannot stop the reader from processing incoming WINDOW_UPDATE frames.
        if (!await _controlSlots.WaitAsync(0))
        {
            await _connectionClosed.CancelAsync();
            throw new IOException("The HTTP/2 peer is not consuming control frames.");
        }

        var request = new WriteRequest(type, flags, streamId, payload.ToArray(), false, CancellationToken.None, true);
        if (!_requests.Writer.TryWrite(request))
        {
            _controlSlots.Release();
            throw new IOException("The HTTP/2 connection writer is closed.");
        }
    }

    public ValueTask QueueWindowUpdateAsync(int streamId, int increment)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, increment);
        return QueueControlAsync(Frames.WindowUpdate, 0, streamId, bytes);
    }

    public ValueTask QueueResetAsync(int streamId, uint errorCode)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, errorCode);
        return QueueControlAsync(Frames.ResetStream, 0, streamId, bytes);
    }

    public ValueTask WriteGoAwayAsync(int lastStreamId, uint errorCode, CancellationToken cancellationToken)
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteInt32BigEndian(bytes, lastStreamId);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(4), errorCode);
        return WriteAsync(Frames.GoAway, 0, 0, bytes, cancellationToken);
    }

    private async ValueTask EnqueueAsync(WriteRequest request)
    {
        await using var registration = request.CancellationToken.UnsafeRegister(static state => ((WriteRequest)state!).Cancel(), request);
        _connectionClosed.Token.ThrowIfCancellationRequested();
        if (!await _applicationSlots.WaitAsync(0))
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(request.CancellationToken, _connectionClosed.Token);
            await _applicationSlots.WaitAsync(cancellation.Token);
        }

        try
        {
            if (!_requests.Writer.TryWrite(request))
            {
                throw new IOException("The HTTP/2 connection writer is closed.");
            }
        }
        catch
        {
            _applicationSlots.Release();
            throw;
        }

        // The caller owns the payload until this completes. Cancellation can
        // remove a queued write, but cannot release a payload already being sent.
        await request.Completion!.Task;
    }

    private async Task RunAsync()
    {
        WriteRequest? current = null;
        try
        {
            while (await _requests.Reader.WaitToReadAsync(_connectionClosed.Token))
            {
                while (_requests.Reader.TryRead(out current))
                {
                    (current.Detached ? _controlSlots : _applicationSlots).Release();
                    if (!current.TryStart())
                    {
                        current = null;
                        continue;
                    }

                    var offset = 0;
                    do
                    {
                        var count = Math.Min(Frames.MaximumPayload, current.Payload.Length - offset);
                        if (_buffered + 9 + count > _buffer.Length)
                        {
                            await FlushAsync();
                        }

                        var type  = current.Type;
                        var flags = current.Flags;
                        if (current.IsHeaderBlock)
                        {
                            type  = offset == 0 ? Frames.Headers : Frames.Continuation;
                            flags = offset == 0 ? current.Flags : (byte)0;
                            if (offset + count == current.Payload.Length)
                            {
                                flags |= Frames.EndHeaders;
                            }
                        }

                        var frame = _buffer.AsMemory(_buffered, 9 + count);
                        Frames.WriteHeader(frame.Span, type, flags, current.StreamId, count);
                        current.Payload.Span.Slice(offset, count).CopyTo(frame.Span[9..]);
                        _buffered += frame.Length;
                        offset    += count;
                    } while (offset < current.Payload.Length);

                    // A header block is copied in full before another request
                    // can contribute frames, including across buffer flushes.
                    if (!current.Detached)
                    {
                        _batch.Add(current);
                    }

                    current = null;
                }

                // Only batch work already available. Streaming writes never
                // wait for a timer or for another response to fill the buffer.
                await FlushAsync();
            }
        }
        catch (Exception exception)
        {
            _requests.Writer.TryComplete(exception);
            try
            {
                // A failed or timed-out write can leave a partial frame on the wire.
                await _connectionClosed.CancelAsync();
            }
            finally
            {
                current?.Completion?.TrySetException(exception);
                foreach (var request in _batch)
                {
                    request.Completion!.TrySetException(exception);
                }

                _batch.Clear();
                while (_requests.Reader.TryRead(out var request))
                {
                    (request.Detached ? _controlSlots : _applicationSlots).Release();
                    request.Completion?.TrySetException(exception);
                }
            }
        }
    }

    private async ValueTask FlushAsync()
    {
        if (_buffered == 0)
        {
            return;
        }

        _deadline.CancelAfter(_writeTimeout);
        try
        {
            await _stream.WriteAsync(_buffer.AsMemory(0, _buffered), _deadline.Token);
        }
        finally
        {
            _deadline.CancelAfter(Timeout.InfiniteTimeSpan);
        }

        _buffered = 0;
        foreach (var request in _batch)
        {
            request.Completion!.TrySetResult();
        }

        _batch.Clear();
    }

    private sealed class WriteRequest
    {
        public  byte                  Type              { get; }
        public  byte                  Flags             { get; }
        public  int                   StreamId          { get; }
        public  ReadOnlyMemory<byte>  Payload           { get; }
        public  bool                  IsHeaderBlock     { get; }
        public  CancellationToken     CancellationToken { get; }
        public  bool                  Detached          { get; }
        public  TaskCompletionSource? Completion        { get; }
        private int                   _state;

        public WriteRequest(
            byte                 type,
            byte                 flags,
            int                  streamId,
            ReadOnlyMemory<byte> payload,
            bool                 isHeaderBlock,
            CancellationToken    cancellationToken,
            bool                 detached = false)
        {
            Type              = type;
            Flags             = flags;
            StreamId          = streamId;
            Payload           = payload;
            IsHeaderBlock     = isHeaderBlock;
            CancellationToken = cancellationToken;
            Detached          = detached;
            Completion        = detached ? null : new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public bool TryStart() => Interlocked.CompareExchange(ref _state, 1, 0) == 0;

        public void Cancel()
        {
            if (Interlocked.CompareExchange(ref _state, 2, 0) == 0)
            {
                Completion!.TrySetCanceled(CancellationToken);
            }
        }
    }
}
