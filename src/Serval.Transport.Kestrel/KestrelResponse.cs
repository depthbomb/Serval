#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using System.Net.WebSockets;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Serval.Http;
using StatusCodes = Serval.Http.Constants.StatusCodes;

namespace Serval.Transport.Kestrel;

internal sealed class KestrelResponse : Response
{
    private bool _upgraded;

    private readonly HttpContext _context;

    public KestrelResponse(HttpContext context)
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

        if (!_context.WebSockets.IsWebSocketRequest)
        {
            throw new HttpErrorException(StatusCodes.BadRequest, "A WebSocket upgrade request is required.");
        }

        await MarkUpgradedAsync(ct);

        ApplyHeaders();

        using var cancellation = AbortOnCancellation(ct);
        var socket = await _context.WebSockets.AcceptWebSocketAsync(subProtocol);
        if (ct.IsCancellationRequested)
        {
            socket.Dispose();
            ct.ThrowIfCancellationRequested();
        }

        _upgraded = true;
        return socket;
    }

    public override async ValueTask FlushAsync(CancellationToken ct = default)
    {
        EnsureCanWrite();
        await StartAsync(ct);
        await _context.Response.Body.FlushAsync(ct);
    }

    public override async ValueTask CompleteAsync(CancellationToken ct = default)
    {
        if (HasEnded)
        {
            return;
        }

        if (!_upgraded)
        {
            ct.ThrowIfCancellationRequested();
            await StartAsync(ct);
            ct.ThrowIfCancellationRequested();
            var completion = _context.Response.CompleteAsync();
            if (!completion.IsCompleted)
            {
                // The native completion API has no token. Abort pending I/O, then await it before releasing the context.
                using var cancellation = AbortOnCancellation(ct);
                await completion;
                ct.ThrowIfCancellationRequested();
            }
            else
            {
                await completion;
            }
        }

        MarkCompleted();
    }

    public override ValueTask AbortAsync(CancellationToken ct = default)
    {
        MarkAborted();

        _context.Abort();

        return ValueTask.CompletedTask;
    }

    private CancellationTokenRegistration AbortOnCancellation(CancellationToken ct)
        => ct.UnsafeRegister(static state => ((HttpContext)state!).Abort(), _context);

    private void ApplyHeaders()
    {
        foreach (var (name, values) in Headers)
        {
            _context.Response.Headers[name] = values.Count == 1 ? new StringValues(values[0]) : new StringValues([.. values]);
        }
    }

    protected override ValueTask OnStartAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        _context.Response.StatusCode = StatusCode;

        ApplyHeaders();

        return new ValueTask(_context.Response.StartAsync(ct));
    }

    protected override ValueTask OnWriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken ct)
        => HttpMethods.IsHead(_context.Request.Method) ? ValueTask.CompletedTask : _context.Response.Body.WriteAsync(bytes, ct);
}
