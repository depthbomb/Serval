#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Serval.Http;
using Serval.Http.Constants;

namespace Serval.SocketIO.Internal;

// ReSharper disable once InconsistentNaming
internal sealed class SocketIOEndpoint : ControllerBase
{
    private readonly SocketIOServer _server;

    public SocketIOEndpoint(SocketIOServer server)
    {
        _server = server;
    }

    internal async ValueTask<IActionResult> HandleAsync(RequestContext context)
    {
        ValidateOrigin(context);
        context.Response.Headers[HeaderNames.CacheControl] = CacheControlDirectives.NoStore;
        if (context.Request.Method == HttpMethods.Options)
        {
            context.Response.Headers[HeaderNames.AccessControlAllowMethods] = HttpMethods.Get         + ", " + HttpMethods.Post + ", " + HttpMethods.Options;
            context.Response.Headers[HeaderNames.AccessControlAllowHeaders] = HeaderNames.ContentType + ", " + HeaderNames.Authorization;
            return StatusCode(StatusCodes.NoContent);
        }

        if (context.Request.Method is not (HttpMethods.Get or HttpMethods.Post) || context.Request.QueryValue("EIO") != "4")
        {
            throw new HttpErrorException(StatusCodes.BadRequest, "Engine.IO version 4 is required.");
        }

        var transport = context.Request.QueryValue("transport");
        var sid       = context.Request.QueryValue("sid", "");
        if (sid.Length == 0 && context.Request.Query.ContainsKey("sid"))
        {
            throw new HttpErrorException(StatusCodes.BadRequest, "The session ID cannot be empty.");
        }
        if (transport == "websocket")
        {
            if (!_server.Options.EnableWebSockets         ||
                context.Request.Method != HttpMethods.Get ||
                !string.Equals(context.Request.Headers[HeaderNames.Upgrade], "websocket", StringComparison.OrdinalIgnoreCase))
            {
                throw new HttpErrorException(StatusCodes.BadRequest, "A WebSocket upgrade request is required.");
            }

            var upgrade = sid.Length > 0;
            var session = upgrade ? _server.Find(sid) : _server.Create(context, true);
            if (upgrade)
            {
                session.BeginUpgrade();
            }

            try
            {
                using var socket = await context.Response.AcceptWebSocketAsync(cancellationToken: context.RequestAborted);
                await session.RunWebSocketAsync(socket, upgrade, context.RequestAborted);
            }
            catch
            {
                if (!upgrade)
                {
                    session.Close("WebSocket handshake failed");
                }

                throw;
            }
            finally
            {
                session.CancelUpgrade();
            }

            return Empty();
        }

        if (transport != "polling")
        {
            throw new HttpErrorException(StatusCodes.BadRequest, "Unknown Engine.IO transport.");
        }

        context.Response.Headers[HeaderNames.ContentType] = MimeTypes.Text;
        if (sid.Length == 0)
        {
            if (context.Request.Method != HttpMethods.Get)
            {
                throw new HttpErrorException(StatusCodes.BadRequest, "A session ID is required.");
            }

            var created = _server.Create(context, false);
            try
            {
                await context.Response.WriteTextAsync(created.OpenPacket(true).PollingText, context.RequestAborted);
            }
            catch
            {
                created.Close("handshake failed");
                throw;
            }

            return Empty();
        }

        var existing = _server.Find(sid);
        if (context.Request.Method == HttpMethods.Get)
        {
            await existing.PollAsync(context.Response, context.RequestAborted);
            return Empty();
        }

        existing.BeginPost();
        try
        {
            using var receive = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, existing.Closed);
            receive.CancelAfter(_server.Options.PayloadTimeout);
            var    reading = context.Request.ReadBodyAsync(receive.Token).AsTask();
            byte[] body;
            try
            {
                body = await reading.WaitAsync(receive.Token);
            }
            catch (OperationCanceledException)
            {
                // Some transport streams only release a pending read when the connection is aborted.
                existing.Close("polling payload timeout or session closed");
                await context.Response.AbortAsync(receive.Token);
                try
                {
                    await reading;
                }
                catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException) {}

                throw;
            }
            if (body.Length > _server.Options.MaxPayloadBytes)
            {
                throw new HttpErrorException(StatusCodes.PayloadTooLarge, "Engine.IO payload is too large.");
            }

            foreach (var text in EnginePacket.Utf8.GetString(body).Split('\x1e'))
            {
                var packet = text.StartsWith('b') ? new EnginePacket(Convert.FromBase64String(text[1..]), true) : EnginePacket.Text(text);
                await existing.ReceiveAsync(packet, receive.Token);
            }

            await context.Response.WriteTextAsync("ok", context.RequestAborted);
            await context.Response.CompleteAsync(context.RequestAborted);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or FormatException or System.Text.DecoderFallbackException)
        {
            existing.Close("invalid polling payload");
            throw new HttpErrorException(StatusCodes.BadRequest, "Invalid Engine.IO payload.", innerException: exception);
        }
        catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
        {
            existing.Close("polling payload timeout or session closed");
            throw new HttpErrorException(StatusCodes.RequestTimeout, "The Engine.IO payload could not be received in time.");
        }
        catch
        {
            existing.Close("polling request failed");
            throw;
        }
        finally
        {
            existing.EndPost();
        }

        return Empty();
    }

    private void ValidateOrigin(RequestContext context)
    {
        var values = context.Request.Headers.GetValues(HeaderNames.Origin);
        if (values.Count == 0)
        {
            return;
        }

        var origin     = values[0];
        var sameOrigin = context.Request.Scheme + "://" + context.Request.Host;
        if (values.Count != 1                                                                                 ||
            !Uri.TryCreate(origin, UriKind.Absolute, out var uri)                                             ||
            uri.Scheme is not ("http" or "https")                                                             ||
            !string.Equals(uri.GetLeftPart(UriPartial.Authority), origin, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(origin, sameOrigin, StringComparison.OrdinalIgnoreCase) && !_server.Options.AllowedOrigins.Contains(origin))
        {
            throw new HttpErrorException(StatusCodes.Forbidden, "This origin is not allowed to connect.");
        }

        context.Response.Headers[HeaderNames.AccessControlAllowOrigin]      = origin;
        context.Response.Headers[HeaderNames.AccessControlAllowCredentials] = "true";
        var vary = context.Response.Headers[HeaderNames.Vary];
        context.Response.Headers[HeaderNames.Vary] = string.IsNullOrEmpty(vary) ? HeaderNames.Origin : vary + ", " + HeaderNames.Origin;
    }
}
