#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using System.Globalization;
using Serval.Http;
using Serval.Http.Constants;
using Serval.Transport.Http2.Protocol;

namespace Serval.Transport.Http2;

internal sealed class Http2Response : Response
{
    public override  bool               SupportsEarlyHints => true;
    private          long?              _contentLength;
    private          bool               _ended;
    private          long               _written;
    private readonly Http2Connection    _connection;
    private readonly Http2RequestStream _request;

    public Http2Response(Http2Connection connection, Http2RequestStream request)
    {
        _connection = connection;
        _request    = request;
    }

    public override ValueTask FlushAsync(CancellationToken cancellationToken = default)
    {
        EnsureCanWrite();
        // FrameWriter completes each awaited write only after its batch reaches the transport.
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    public override async ValueTask CompleteAsync(CancellationToken cancellationToken = default)
    {
        if (HasEnded)
        {
            return;
        }

        await StartAsync(cancellationToken);
        if (!_ended)
        {
            if (_contentLength is { } expected && expected != _written)
            {
                throw new Http2ProtocolException(Http2Error.Protocol, "Response Content-Length does not match the body.", _request.Id);
            }

            await _connection.EndResponseAsync(_request, cancellationToken);
            _ended = true;
        }

        MarkCompleted();
    }

    public override ValueTask AbortAsync(CancellationToken cancellationToken = default)
    {
        MarkAborted();
        return _connection.AbortResponseAsync(_request);
    }

    protected override ValueTask OnSendEarlyHintsAsync(Headers headers, CancellationToken ct)
        => _connection.WriteHeadersAsync(_request, StatusCodes.EarlyHints, headers, false, ct);

    protected override async ValueTask OnStartAsync(CancellationToken cancellationToken)
    {
        if (Headers[HeaderNames.ContentLength] is { } length)
        {
            if (!long.TryParse(length, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
            {
                throw new InvalidOperationException("Invalid response Content-Length.");
            }

            _contentLength = parsed;
        }

        // End bodyless responses on HEADERS. A HEAD Content-Length describes the GET body, not bytes to send here.
        _ended = _request.Head.Method == HttpMethods.Head || StatusCode is StatusCodes.NoContent or StatusCodes.NotModified;
        await _connection.WriteHeadersAsync(_request, StatusCode, Headers, _ended, cancellationToken);
    }

    protected override async ValueTask OnWriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        if (_ended)
        {
            return;
        }

        if (_contentLength is { } expected && bytes.Length > expected - _written)
        {
            throw new Http2ProtocolException(Http2Error.Protocol, "Response DATA exceeds Content-Length.", _request.Id);
        }

        await _connection.WriteDataAsync(_request, bytes, cancellationToken);
        _written += bytes.Length;
    }
}
