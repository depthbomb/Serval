#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Serval.Http;
using Serval.Http.Constants;

namespace Serval.Transport.Http3.Protocol;

internal sealed class Http3BodyStream : Stream
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
    private          bool   _ended;
    private          long   _received;
    private          long   _remaining;
    private          bool   _trailers;
    private readonly long?  _contentLength;
    private readonly long   _maximum;
    private readonly int    _maximumHeaders;
    private readonly Stream _stream;

    public Http3BodyStream(Stream stream, long maximum, int maximumHeaders, long? contentLength)
    {
        _stream         = stream;
        _maximum        = maximum;
        _maximumHeaders = maximumHeaders;
        _contentLength  = contentLength;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.Length == 0 || _ended)
        {
            return 0;
        }

        while (_remaining == 0)
        {
            var frame = await Frames.ReadAsync(_stream, cancellationToken);
            if (frame is null)
            {
                _ended = true;
                if (_contentLength is {} expected && _received != expected)
                {
                    throw new Http3ProtocolException(Http3Error.Message, "Content-Length does not match the DATA payload.");
                }

                return 0;
            }

            switch (frame.Value.Type)
            {
                case Frames.Data:
                    if (_trailers)
                    {
                        throw new Http3ProtocolException(Http3Error.FrameUnexpected, "DATA after trailers.");
                    }

                    _remaining = frame.Value.Length;
                    if (_remaining > _maximum - _received)
                    {
                        throw new HttpErrorException(StatusCodes.PayloadTooLarge);
                    }

                    if (_contentLength is {} expected && _remaining > expected - _received)
                    {
                        throw new Http3ProtocolException(Http3Error.Message, "DATA exceeds Content-Length.");
                    }

                    break;
                case Frames.Headers:
                    if (_trailers)
                    {
                        throw new Http3ProtocolException(Http3Error.FrameUnexpected, "Multiple trailer sections.");
                    }

                    var payload = await Frames.ReadPayloadAsync(_stream, frame.Value.Length, _maximumHeaders, cancellationToken);
                    var fields  = QPack.Decode(payload, _maximumHeaders);
                    foreach (var (name, value) in fields)
                    {
                        try
                        {
                            Headers.ValidateTrailer(name, value);
                        }
                        catch (ArgumentException)
                        {
                            throw new Http3ProtocolException(Http3Error.Message, "Invalid trailer field.");
                        }
                    }

                    _trailers = true;
                    break;
                default:
                    RejectForbiddenFrame(frame.Value.Type);
                    await Frames.SkipAsync(_stream, frame.Value.Length, cancellationToken);
                    break;
            }
        }

        // Expose only DATA bytes to the app; the next frame's header stays for the framing loop above.
        var read = await _stream.ReadAsync(buffer[..(int)Math.Min(buffer.Length, _remaining)], cancellationToken);
        if (read == 0)
        {
            throw new Http3ProtocolException(Http3Error.Frame, "Truncated DATA frame.");
        }

        _remaining -= read;
        _received  += read;
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("Use asynchronous I/O.");

    public override void Flush() => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public static void RejectForbiddenFrame(long type)
    {
        if (type is 2 or 3 or 4 or 5 or 6 or 7 or 8 or 9 or 13)
        {
            throw new Http3ProtocolException(Http3Error.FrameUnexpected, "Forbidden frame on a request stream.");
        }
    }
}
