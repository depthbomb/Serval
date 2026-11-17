#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Net.WebSockets;
using System.Text.Json.Serialization;
using Serval.Http;
using Serval.Http.Constants;
using Serval.Middleware;
using Serval.Routing;

namespace Serval.Sample.Transports;

public sealed record Widget(int Id, string Name, string Url);

public sealed record CreateWidget(string? Name);

[JsonSerializable(typeof(Widget))]
[JsonSerializable(typeof(CreateWidget))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal partial class SampleJsonContext : JsonSerializerContext;

// Generated routes invoke scoped controller instances.
[Controller]
[Route("/api")]
[ResponseHeader("X-Serval", "sample")]
public sealed class ApiController : ControllerBase
{
    [Get("widgets/{id:int:min(1)}", Name = "widgets.get")]
    public ValueTask<IActionResult> GetWidgetAsync(RequestContext context)
    {
        var id = RouteValue<int>("id");
        var url = context.Urls.Path("widgets.get", new Dictionary<string, object?>
        {
            ["id"] = id
        });

        return JsonAsync(new Widget(id, $"Widget {id}", url), SampleJsonContext.Default.Widget);
    }

    [Post("widgets")]
    public async ValueTask<IActionResult> CreateWidgetAsync(RequestContext context)
    {
        var input = await context.Request.ReadJsonAsync(SampleJsonContext.Default.CreateWidget, context.RequestAborted);
        AbortIf(string.IsNullOrWhiteSpace(input.Name), StatusCodes.UnprocessableContent, "A widget name is required.");

        var url = context.Urls.Path("widgets.get", new Dictionary<string, object?>
        {
            ["id"] = 1
        });
        context.Response.Headers[HeaderNames.Location] = url;

        return Json(new Widget(1, input.Name, url), SampleJsonContext.Default.Widget, StatusCodes.Created);
    }

    [Get("cookie")]
    public ValueTask<IActionResult> SetCookieAsync(RequestContext context)
    {
        // The sample's HTTP listener uses loopback HTTP. Production cookies default to Secure.
        context.Response.Cookies.Set("theme", "dark", new CookieOptions
        {
            Secure = context.Request.Url.Scheme == "https"
        });

        return TextAsync("Cookie set.");
    }

    [Get("download")]
    public ValueTask<IActionResult> DownloadAsync(RequestContext context)
        => FileAsync(Path.Combine(AppContext.BaseDirectory, "Public", "hello.json"), MimeTypes.Json, "hello.json");

    [Get("events")]
    public async ValueTask<IActionResult> EventsAsync(RequestContext context)
    {
        context.DisableRequestTimeout();

        var events = await context.Response.StartEventsAsync(context.RequestAborted);
        for (var index = 0; index < 3; index++)
        {
            await events.SendAsync($"Event {index}", "tick", index.ToString(CultureInfo.InvariantCulture), cancellationToken: context.RequestAborted);
            await Task.Delay(500, context.RequestAborted);
        }

        return Empty();
    }

    [Get("socket")]
    public async ValueTask<IActionResult> SocketAsync(RequestContext context)
    {
        context.DisableRequestTimeout();

        using var socket = await context.Response.AcceptWebSocketAsync(cancellationToken: context.RequestAborted);
        var       buffer = new byte[4096];
        while (socket.State == WebSocketState.Open)
        {
            var received = await socket.ReceiveAsync(buffer.AsMemory(), context.RequestAborted);
            if (received.MessageType == WebSocketMessageType.Close)
            {
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Closed", context.RequestAborted);
                break;
            }

            await socket.SendAsync(buffer.AsMemory(0, received.Count), received.MessageType, received.EndOfMessage, context.RequestAborted);
        }

        return Empty();
    }
}

public sealed class ResponseHeaderAttribute : MiddlewareAttribute
{
    private readonly string _name;
    private readonly string _value;

    public ResponseHeaderAttribute(string name, string value)
    {
        _name  = name;
        _value = value;
    }

    public override ValueTask InvokeAsync(RequestContext context, RequestDelegate next)
    {
        var logger = context.Services.GetRequiredService<ILogger<ResponseHeaderAttribute>>();
        if (logger.IsEnabled(LogLevel.Debug))
        {
            logger.LogDebug("Applying response header {Header} to {Path}.", _name, context.Request.Url.AbsolutePath);
        }

        context.Response.Headers[_name] = _value;
        return next(context);
    }
}
