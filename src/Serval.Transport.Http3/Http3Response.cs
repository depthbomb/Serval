#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using System.Globalization;
using System.Net.Quic;
using System.Runtime.Versioning;
using Serval.Http;
using Serval.Http.Constants;
using Serval.Transport.Http3.Protocol;

namespace Serval.Transport.Http3;

[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
internal sealed class Http3Response : Response
{
    public override  bool       SupportsEarlyHints => true;
    private          long?      _contentLength;
    private          bool       _ended;
    private          bool       _suppressBody;
    private          long       _written;
    private readonly bool       _head;
    private readonly QuicStream _stream;

    public Http3Response(QuicStream stream, bool head)
    {
        _stream = stream;
        _head   = head;
    }

    public override ValueTask FlushAsync(CancellationToken cancellationToken = default)
    {
        EnsureCanWrite();
        return new ValueTask(_stream.FlushAsync(cancellationToken));
    }

    public override async ValueTask CompleteAsync(CancellationToken cancellationToken = default)
    {
        if (HasEnded)
        {
            return;
        }

        await StartAsync(cancellationToken);
        if (_ended)
        {
            return;
        }

        // Check both ends of the declared length so intermediaries cannot accept ambiguous framing.
        if (!_suppressBody && _contentLength is {} expected && _written != expected)
        {
            throw new Http3ProtocolException(Http3Error.Message, "Response Content-Length does not match the body.");
        }

        _stream.CompleteWrites();
        await _stream.WritesClosed.WaitAsync(cancellationToken);
        _ended = true;
        MarkCompleted();
    }

    public override ValueTask AbortAsync(CancellationToken cancellationToken = default)
    {
        MarkAborted();
        _stream.Abort(QuicAbortDirection.Both, Http3Error.RequestCancelled);
        return ValueTask.CompletedTask;
    }

    protected override async ValueTask OnSendEarlyHintsAsync(Headers headers, CancellationToken ct)
    {
        await Frames.WriteAsync(_stream, Frames.Headers, QPack.Encode(StatusCodes.EarlyHints, headers), ct);
        await _stream.FlushAsync(ct);
    }

    protected override ValueTask OnStartAsync(CancellationToken cancellationToken)
    {
        if (Headers[HeaderNames.ContentLength] is {} length)
        {
            if (!long.TryParse(length, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
            {
                throw new InvalidOperationException("Invalid response Content-Length.");
            }

            _contentLength = parsed;
        }

        _suppressBody = _head || StatusCode is StatusCodes.NoContent or StatusCodes.NotModified;
        return Frames.WriteAsync(_stream, Frames.Headers, QPack.Encode(StatusCode, Headers), cancellationToken);
    }

    protected override async ValueTask OnWriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        if (_suppressBody)
        {
            return;
        }

        if (_contentLength is {} expected && bytes.Length > expected - _written)
        {
            throw new Http3ProtocolException(Http3Error.Message, "Response DATA exceeds Content-Length.");
        }

        await Frames.WriteAsync(_stream, Frames.Data, bytes, cancellationToken);
        _written += bytes.Length;
    }
}
