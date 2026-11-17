#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

namespace Serval.Http;

public sealed partial class Request
{
    /// <summary>Leases the remaining body as an asynchronous, bounded read stream.</summary>
    /// <remarks>
    /// Dispose the lease before using another raw reader. Reads share cumulative limits and request cancellation.
    /// The transport stream is never closed. Form and multipart modes cannot be mixed with raw reading.
    /// The lease and its reads must not be used concurrently. Select the endpoint before opening the lease.
    /// </remarks>
    public Stream OpenBodyStream(CancellationToken ct = default) => OpenBodyStreamCore(ct, false);

    private Stream OpenBodyStreamCore(CancellationToken ct, bool multipart)
    {
        ct.ThrowIfCancellationRequested();

        RequestAborted.ThrowIfCancellationRequested();

        lock (_bodyGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_dataTask is not null || _readingBody || _streamedMultipart || multipart && _rawBodyRead)
            {
                throw new InvalidOperationException("The body is already reserved for another reader.");
            }

            var stream = new BodyReadStream(this, ct, true);
            _readingBody       = true;
            _rawBodyRead       = true;
            _streamedMultipart = multipart;
            return stream;
        }
    }

    private sealed class BodyReadStream : Stream
    {
        public override bool CanRead  => !_disposed;
        public override bool CanSeek  => false;
        public override bool CanWrite => false;
        public override long Length   => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        private          bool                    _disposed;
        private          int                     _reading;
        private readonly Request                 _request;
        private readonly CancellationTokenSource _cts;
        private readonly bool                    _release;

        internal BodyReadStream(Request request, CancellationToken ct, bool release)
        {
            _request = request;
            _cts     = CancellationTokenSource.CreateLinkedTokenSource(ct, request.RequestAborted);
            _release = release;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (Interlocked.CompareExchange(ref _reading, 1, 0) != 0)
            {
                throw new InvalidOperationException("Body reads must be sequential.");
            }

            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, cancellationToken);
                return await _request.ReadBodyChunkAsync(buffer, linked.Token);
            }
            finally
            {
                Volatile.Write(ref _reading, 0);
            }
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count)
            => throw new NotSupportedException("Use asynchronous body reads.");

        public override void Flush() => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (!disposing || _disposed)
            {
                return;
            }

            if (Volatile.Read(ref _reading) != 0)
            {
                throw new InvalidOperationException("Await the body read before disposing its lease.");
            }

            _disposed = true;
            _cts.Dispose();
            if (_release)
            {
                lock (_request._bodyGate)
                {
                    _request._readingBody = false;
                }
            }

            base.Dispose(disposing);
        }
    }
}
