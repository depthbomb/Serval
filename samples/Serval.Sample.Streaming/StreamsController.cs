#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Serval.Http;
using Serval.Http.Constants;
using Serval.Routing;

namespace Serval.Sample.Streaming;

[Controller]
public sealed class StreamsController : ControllerBase
{
    [Get("/")]
    public ValueTask<IActionResult> IndexAsync(RequestContext context) => TextAsync("""
                                                                                    Streaming recipes
                                                                                    GET /events        SSE events 1 through 10, one every 250 ms, with heartbeats
                                                                                    Send Last-Event-ID: 7 to resume this deterministic sequence at event 8.
                                                                                    A completed SSE sequence returns 204 on reconnect with Last-Event-ID: 10.
                                                                                    GET /records       Five newline-delimited JSON records, flushed individually
                                                                                    GET /socket        WebSocket echo; text or binary, including fragmented messages
                                                                                    The WebSocket message limit is 64 KiB, with a two-minute idle timeout.
                                                                                    Use curl -N for HTTP streams and a WebSocket client for /socket.
                                                                                    """);

    [Get("/events")]
    public async ValueTask<IActionResult> EventsAsync(RequestContext context)
    {
        var last     = 0;
        var supplied = context.Request.Headers.GetValues("Last-Event-ID");
        AbortIf(supplied.Count > 1 || supplied.Count == 1
            && (!int.TryParse(supplied[0], NumberStyles.None, CultureInfo.InvariantCulture, out last) || last is < 0 or > 10),
            StatusCodes.BadRequest, "Last-Event-ID must be an integer from 0 through 10.");
        if (last == 10)
        {
            return StatusCode(StatusCodes.NoContent);
        }

        context.DisableRequestTimeout();
        var events = await context.Response.StartEventsAsync(context.RequestAborted);
        for (var id = last + 1; id <= 10; id++)
        {
            await events.HeartbeatAsync(context.RequestAborted);
            await events.SendAsync($"Tick {id}", "tick", id.ToString(CultureInfo.InvariantCulture), TimeSpan.FromSeconds(1), context.RequestAborted);
            await Task.Delay(250, context.RequestAborted);
        }

        return Empty();
    }

    [Get("/records")]
    public async ValueTask<IActionResult> RecordsAsync(RequestContext context)
    {
        context.DisableRequestTimeout();
        context.Response.Headers[HeaderNames.ContentType]  = "application/x-ndjson; charset=utf-8";
        context.Response.Headers[HeaderNames.CacheControl] = "no-store";
        for (var index = 1; index <= 5; index++)
        {
            var json = JsonSerializer.Serialize(new { index, message = $"Record {index}" });
            await context.Response.WriteAsync(Encoding.UTF8.GetBytes(json + "\n"), context.RequestAborted);
            await context.Response.FlushAsync(context.RequestAborted);
            await Task.Delay(100, context.RequestAborted);
        }

        return Empty();
    }

    [Get("/socket")]
    public async ValueTask<IActionResult> SocketAsync(RequestContext context)
    {
        context.DisableRequestTimeout();
        using var socket       = await context.Response.AcceptWebSocketAsync(cancellationToken: context.RequestAborted);
        using var idle         = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        var       buffer       = new byte[4096];
        var       messageBytes = 0;
        try
        {
            while (socket.State == WebSocketState.Open)
            {
                idle.CancelAfter(TimeSpan.FromMinutes(2));
                var received = await socket.ReceiveAsync(buffer.AsMemory(), idle.Token);
                if (received.MessageType == WebSocketMessageType.Close)
                {
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Goodbye", idle.Token);
                    break;
                }

                messageBytes += received.Count;
                if (messageBytes > 64 * 1024)
                {
                    // Wait briefly for the peer's close reply before disposing the socket.
                    idle.CancelAfter(TimeSpan.FromSeconds(5));
                    await socket.CloseAsync(WebSocketCloseStatus.MessageTooBig, "Messages must fit in 64 KiB.", idle.Token);
                    break;
                }

                await socket.SendAsync(buffer.AsMemory(0, received.Count), received.MessageType, received.EndOfMessage, idle.Token);
                if (received.EndOfMessage)
                {
                    messageBytes = 0;
                }
            }
        }
        catch (OperationCanceledException) when (idle.IsCancellationRequested)
        {
            socket.Abort();
        }
        catch (WebSocketException)
        {
            // A disconnected peer cannot receive an HTTP error after the upgrade.
            socket.Abort();
        }

        return Empty();
    }
}
