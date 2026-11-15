#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using System.Buffers;
using System.Runtime.ExceptionServices;

namespace Serval.Transport.Http2.Protocol;

internal sealed class Http2BodyStream : Stream
{
    public override bool CanRead  => true;
    public override bool CanSeek  => false;
    public override bool CanWrite => false;
    public override long Length   => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }
    private          byte[]?                                 _buffer;
    private          TaskCompletionSource                    _changed = NewSignal();
    private          int                                     _count;
    private          bool                                    _discarding;
    private          bool                                    _ended;
    private          Exception?                              _failure;
    private          int                                     _head;
    private readonly Func<int, CancellationToken, ValueTask> _consumed;
    private readonly Lock                                    _gate = new();

    public Http2BodyStream(Func<int, CancellationToken, ValueTask> consumed)
    {
        _consumed = consumed;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken cancellationToken = default)
    {
        if (destination.Length == 0)
        {
            return 0;
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Task wait;
            var  read = 0;
            lock (_gate)
            {
                if (_failure is not null)
                {
                    ExceptionDispatchInfo.Throw(_failure);
                }

                if (_count > 0)
                {
                    read = Math.Min(_count, destination.Length);
                    var first = Math.Min(read, Frames.InitialWindow - _head);
                    _buffer!.AsMemory(_head, first).CopyTo(destination);
                    _buffer.AsMemory(0, read - first).CopyTo(destination[first..]);
                    _head  =  (_head         + read) % Frames.InitialWindow;
                    _count -= read;
                }
                else if (_ended || _discarding)
                {
                    return 0;
                }

                wait = _changed.Task;
            }

            if (read > 0)
            {
                // Returning stream credit can enqueue network work, so do it after releasing the buffer lock.
                await _consumed(read, cancellationToken);
                return read;
            }

            await wait.WaitAsync(cancellationToken);
        }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("Use asynchronous I/O.");

    public override void Flush() => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public bool Append(ReadOnlySpan<byte> data)
    {
        lock (_gate)
        {
            if (_discarding)
            {
                return false;
            }

            if (data.Length > Frames.InitialWindow - _count)
            {
                throw new InvalidOperationException("The receive buffer exceeded its flow-control window.");
            }

            if (data.Length == 0)
            {
                return true;
            }

            // One receive window bounds unread bytes. Reuse a ring buffer instead of allocating for every DATA frame.
            _buffer ??= ArrayPool<byte>.Shared.Rent(Frames.InitialWindow);
            var tail  = (_head + _count) % Frames.InitialWindow;
            var first = Math.Min(data.Length, Frames.InitialWindow - tail);
            data[..first].CopyTo(_buffer.AsSpan(tail));
            data[first..].CopyTo(_buffer);
            _count += data.Length;
            Signal();
            return true;
        }
    }

    public void Complete(Exception? exception = null)
    {
        lock (_gate)
        {
            _ended   =   true;
            _failure ??= exception;
            Signal();
        }
    }

    public int Discard()
    {
        lock (_gate)
        {
            _discarding = true;
            var discarded = _count;
            _count = 0;
            Signal();
            return discarded;
        }
    }

    private void Signal()
    {
        var previous = _changed;
        _changed = NewSignal();
        previous.TrySetResult();
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    protected override void Dispose(bool disposing)
    {
        lock (_gate)
        {
            if (_buffer is not null)
            {
                ArrayPool<byte>.Shared.Return(_buffer);
                _buffer = null;
            }

            _discarding = true;
            _count      = 0;
            Signal();
        }

        base.Dispose(disposing);
    }
}
