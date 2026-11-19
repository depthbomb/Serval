#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

namespace Serval.Http;

// One request owns the buffer. File parts are read-only slices, so large multipart bodies are stored only once.
internal sealed class RequestBodyStorage : IAsyncDisposable
{
    internal long Length => _stream.Length;
    private  bool _disposed;

    private          Stream        _stream = new MemoryStream();
    private readonly string        _directory;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private readonly int _threshold;

    internal RequestBodyStorage(FormOptions options)
    {
        _threshold = options.MemoryThreshold;
        _directory = options.TempDirectory;
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (!_disposed)
            {
                _disposed = true;
                await _stream.DisposeAsync();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async ValueTask AppendAsync(ReadOnlyMemory<byte> bytes, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_stream is MemoryStream memory && bytes.Length > _threshold - memory.Length)
        {
            var path = Path.Combine(_directory, "serval-body-" + Guid.NewGuid().ToString("N") + ".tmp");
            var fileOptions = new FileStreamOptions
            {
                Mode       = FileMode.CreateNew,
                Access     = FileAccess.ReadWrite,
                Share      = FileShare.Read | FileShare.Delete,
                BufferSize = 16_384,
                Options    = FileOptions.Asynchronous | FileOptions.DeleteOnClose,
            };
            if (!OperatingSystem.IsWindows())
            {
                // Request bodies can contain credentials. Restrict access at creation, before writing any bytes.
                fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }

            var file = new FileStream(path, fileOptions);
            try
            {
                memory.Position = 0;
                await memory.CopyToAsync(file, ct);
            }
            catch
            {
                await file.DisposeAsync();
                throw;
            }

            await memory.DisposeAsync();
            _stream = file;
        }

        await _stream.WriteAsync(bytes, ct);
    }

    internal Stream OpenRead(long offset, long length)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (offset < 0 || length < 0 || offset > Length - length)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        return new Slice(this, offset, length);
    }

    private int Read(long offset, Span<byte> buffer)
    {
        _gate.Wait();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _stream.Position = offset;
            return _stream.Read(buffer);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _stream.Position = offset;
            return await _stream.ReadAsync(buffer, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private sealed class Slice : Stream
    {
        public override bool CanRead  => !_closed;
        public override bool CanSeek  => !_closed;
        public override bool CanWrite => false;
        public override long Length   { get; }
        public override long Position
        {
            get => _position;
            set => Seek(value, SeekOrigin.Begin);
        }
        private bool _closed;

        private          long _position;
        private readonly long _offset;

        private readonly RequestBodyStorage _owner;

        internal Slice(RequestBodyStorage owner, long offset, long length)
        {
            _owner  = owner;
            _offset = offset;
            Length  = length;
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            var count = (int)Math.Min(buffer.Length, Length - _position);
            var read  = _owner.Read(_offset + _position, buffer[..count]);
            _position += read;
            return read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
            => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            var count = (int)Math.Min(buffer.Length, Length - _position);
            var read  = await _owner.ReadAsync(_offset      + _position, buffer[..count], ct);
            _position += read;
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            var position = checked(origin switch
            {
                SeekOrigin.Begin   => 0,
                SeekOrigin.Current => _position,
                SeekOrigin.End     => Length,
                _                  => throw new ArgumentOutOfRangeException(nameof(origin)),
            } + offset);
            if (position < 0 || position > Length)
            {
                throw new IOException("Cannot seek outside the uploaded file.");
            }

            return _position = position;
        }

        public override void Flush() {}

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing) => _closed = true;
    }
}
