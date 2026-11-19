#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.AspNetCore.Http;
using Serval.Http;

namespace Serval.Transport.Kestrel;

// Preserve HTTP status codes from Kestrel's body parser when Serval handles an application read failure.
internal sealed class KestrelRequestBodyStream : Stream
{
    public override bool CanRead  => _body.CanRead;
    public override bool CanSeek  => false;
    public override bool CanWrite => false;
    public override long Length  => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    private readonly Stream _body;

    public KestrelRequestBodyStream(Stream body)
    {
        _body = body;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        try
        {
            return _body.Read(buffer, offset, count);
        }
        catch (BadHttpRequestException exception)
        {
            throw Translate(exception);
        }
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        try
        {
            return await _body.ReadAsync(buffer, ct);
        }
        catch (BadHttpRequestException exception)
        {
            throw Translate(exception);
        }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    private static HttpErrorException Translate(BadHttpRequestException exception)
        => new(exception.StatusCode, "The HTTP request body was rejected by the server.", innerException: exception);
}
