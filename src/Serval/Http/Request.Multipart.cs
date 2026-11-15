#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Serval.Http.Constants;

namespace Serval.Http;

public sealed partial class Request
{
    /// <summary>Processes multipart sections sequentially without buffering the entire request or creating temporary files.</summary>
    /// <remarks>
    ///     Read each section's Body asynchronously inside the callback. Unread bytes are drained and validated before the next
    ///     callback. All form and request limits apply, including to skipped sections and epilogues. Choose this mode before
    ///     any other body reader. Failure consumes the request; application-owned destinations may contain partial data.
    /// </remarks>
    public async ValueTask ReadMultipartAsync(Func<MultipartSection, CancellationToken, ValueTask> readSection, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(readSection);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, RequestAborted);
        linked.Token.ThrowIfCancellationRequested();
        var type = FormDataParser.ContentType(Headers[HeaderNames.ContentType]);
        if (!string.Equals(type.MediaType, MimeTypes.MultipartFormData, StringComparison.OrdinalIgnoreCase))
        {
            throw new HttpErrorException(StatusCodes.UnsupportedMediaType, "A multipart form content type is required.");
        }

        lock (_bodyGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_rawBodyRead || _readingBody || _dataTask is not null)
            {
                throw new InvalidOperationException("Choose streaming multipart before consuming the request body.");
            }

            FormOptions.Validate();
            _rawBodyRead       = true;
            _readingBody       = true;
            _streamedMultipart = true;
        }

        try
        {
            await using var input = new MultipartInputStream(this);
            await FormDataParser.StreamMultipartAsync(input, type, FormOptions, readSection, linked.Token);
        }
        finally
        {
            lock (_bodyGate)
            {
                _readingBody = false;
            }
        }
    }

    private async ValueTask<int> ReadBodyChunkAsync(Memory<byte> buffer, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ct.ThrowIfCancellationRequested();
        if (_bodyTooLarge)
        {
            throw new HttpErrorException(StatusCodes.PayloadTooLarge, "The request body is too large.");
        }

        var read = await _body.ReadAsync(buffer, ct);
        if (read > _maxBodyBytes - _bodyBytesRead)
        {
            _bodyTooLarge = true;
            throw new HttpErrorException(StatusCodes.PayloadTooLarge, "The request body is too large.");
        }

        _bodyBytesRead += read;
        return read;
    }

    private sealed class MultipartInputStream : Stream
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

        private readonly Request _request;

        internal MultipartInputStream(Request request)
        {
            _request = request;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => _request.ReadBodyChunkAsync(buffer, ct);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush() => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
