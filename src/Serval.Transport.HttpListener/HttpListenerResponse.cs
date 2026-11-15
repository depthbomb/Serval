#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using System.Globalization;
using System.Net;
using System.Net.WebSockets;
using Serval.Http;
using Serval.Http.Constants;

namespace Serval.Transport.HttpListener;

internal sealed class HttpListenerResponse : Response
{
    private          bool                _upgraded;
    private readonly HttpListenerContext _context;

    public HttpListenerResponse(HttpListenerContext context)
    {
        _context = context;
    }

    public override async ValueTask<WebSocket> AcceptWebSocketAsync(string? subProtocol = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (HasStarted)
        {
            throw new InvalidOperationException("The response has already started.");
        }

        if (!_context.Request.IsWebSocketRequest)
        {
            throw new HttpErrorException(StatusCodes.BadRequest, "A WebSocket upgrade request is required.");
        }

        await MarkUpgradedAsync(ct);
        ApplyHeaders();
        var socket = await _context.AcceptWebSocketAsync(subProtocol);
        _upgraded = true;
        return socket.WebSocket;
    }

    public override ValueTask FlushAsync(CancellationToken ct = default)
    {
        EnsureCanWrite();
        return new ValueTask(_context.Response.OutputStream.FlushAsync(ct));
    }

    public override async ValueTask CompleteAsync(CancellationToken ct = default)
    {
        if (HasEnded)
        {
            return;
        }

        // After an upgrade, the WebSocket owns the connection and normal HTTP completion would close it.
        if (!_upgraded)
        {
            await StartAsync(ct);
            // HttpListener has no awaitable close. Keep its final framing here;
            // body reads and writes use the asynchronous stream APIs.
            _context.Response.Close();
        }

        MarkCompleted();
    }

    public override ValueTask AbortAsync(CancellationToken ct = default)
    {
        MarkAborted();
        _context.Response.Abort();
        return ValueTask.CompletedTask;
    }

    private void ApplyHeaders()
    {
        // HttpListener routes these restricted headers through dedicated properties, not its general header collection.
        foreach (var header in Headers)
        {
            if (header.Key.Equals(HeaderNames.ContentLength, StringComparison.OrdinalIgnoreCase))
            {
                _context.Response.ContentLength64 = long.Parse(header.Value.Single(), CultureInfo.InvariantCulture);
            }
            else if (header.Key.Equals(HeaderNames.ContentType, StringComparison.OrdinalIgnoreCase))
            {
                _context.Response.ContentType = header.Value.Single();
            }
            else
            {
                foreach (var value in header.Value)
                {
                    _context.Response.Headers.Add(header.Key, value);
                }
            }
        }
    }

    protected override ValueTask OnStartAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        _context.Response.StatusCode = StatusCode;
        ApplyHeaders();

        _context.Response.SendChunked = _context.Request.HttpMethod != HttpMethods.Head && StatusCode is not (StatusCodes.NoContent or StatusCodes.NotModified) && Headers[HeaderNames.ContentLength] is null;
        return ValueTask.CompletedTask;
    }

    protected override ValueTask OnWriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken ct)
        => _context.Request.HttpMethod == HttpMethods.Head ? ValueTask.CompletedTask : _context.Response.OutputStream.WriteAsync(bytes, ct);
}
