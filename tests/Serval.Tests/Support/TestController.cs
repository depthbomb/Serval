#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.Extensions.DependencyInjection;
using System.Globalization;
using System.Text.Json.Serialization;
using Serval.Http;
using Serval.Http.Constants;
using Serval.Http.Results;
using Serval.Middleware;
using Serval.Routing;

namespace Serval.Tests.Support;

public sealed record Message(string Text);

[JsonSerializable(typeof(Message))]
public partial class TestJsonContext : JsonSerializerContext;

// Generated routes invoke scoped controller instances.
[Controller]
[Route("test")]
[Trace("controller", Priority = -10)]
public sealed class TestController(ScopedProbe probe) : ControllerBase
{
    [Get("items/{id:int:min(1)}", Name = "item")]
    [Trace("method")]
    public ValueTask<IActionResult> ItemAsync(RequestContext context) => TextAsync($"{RouteValue<int>("id")}:{string.Join(',', (List<string>)context.Items["trace"]!)}");

    [Get("middleware-priority")]
    [Trace("last", 100)]
    [Trace("default")]
    [Trace("first", Priority = -100)]
    [Trace("controller-tie", -10)]
    [Trace("default-tie")]
    public ValueTask<IActionResult> MiddlewarePriorityAsync(RequestContext context) => TextAsync(string.Join(',', (List<string>)context.Items["trace"]!));

    [Get("items/new")]
    public ValueTask<IActionResult> NewAsync(RequestContext context) => TextAsync("new");

    [Get("scoped")]
    [ScopedService]
    [UseMiddleware(typeof(InjectedMiddleware), "X-Probe", 42)]
    public ValueTask<IActionResult> ScopedAsync(RequestContext context) => TextAsync(probe.Id.ToString());

    [Get("task")]
    public Task<IActionResult> TaskAsync(RequestContext context) => TextAsync("task").AsTask();

    [Get("typed")]
    public ValueTask<IActionResult> TypedAsync(RequestContext context) => JsonAsync(new Message("typed"));

    [Get("custom-result")]
    public ValueTask<CustomResult> CustomResultAsync() => ValueTask.FromResult(new CustomResult());

    [Get("result-lifetime")]
    [UseMiddleware(typeof(OuterLifetimeMiddleware))]
    [UseMiddleware(typeof(AsyncLifetimeMiddleware))]
    public ValueTask<LifetimeResult> LifetimeAsync() => ValueTask.FromResult(new LifetimeResult(false));

    [Get("result-lifetime-error")]
    [UseMiddleware(typeof(OuterLifetimeMiddleware))]
    [UseMiddleware(typeof(AsyncLifetimeMiddleware))]
    public ValueTask<LifetimeResult> LifetimeErrorAsync() => ValueTask.FromResult(new LifetimeResult(true));

    [Get("null-result")]
    public ValueTask<IActionResult> NullResultAsync() => ValueTask.FromResult<IActionResult>(null!);

    [Get("status-result")]
    public ValueTask<IActionResult> StatusResultAsync(RequestContext context) => ValueTask.FromResult<IActionResult>(StatusCode(StatusCodes.NoContent));

    [Get("redirect-result")]
    public ValueTask<IActionResult> RedirectResultAsync(RequestContext context) => ValueTask.FromResult<IActionResult>(Redirect("/test/task"));

    [Get("headers")]
    public ValueTask<IActionResult> HeadersAsync(RequestContext context)
    {
        context.Response.Headers["X-Large"] = new string('a', 20_000);
        return TextAsync("headers");
    }

    [Get("not-found.txt")]
    public ValueTask<IActionResult> NotFoundAsync() => ValueTask.FromException<IActionResult>(new HttpErrorException(StatusCodes.NotFound, "Controller-owned 404."));

    [Post("json")]
    public async ValueTask<IActionResult> JsonAsync(RequestContext context)
    {
        var message = await context.Request.ReadJsonAsync(TestJsonContext.Default.Message, context.RequestAborted);
        return Json(message, TestJsonContext.Default.Message);
    }

    [Get("error")]
    public ValueTask<IActionResult> ErrorAsync() => ValueTask.FromException<IActionResult>(new HttpErrorException(StatusCodes.UnprocessableContent, "A safe error.", "validation_failed"));

    [Get("crash")]
    public ValueTask<IActionResult> CrashAsync() => ValueTask.FromException<IActionResult>(new InvalidOperationException("secret implementation details"));

    [Get("partial")]
    public async ValueTask<IActionResult> PartialAsync(RequestContext context)
    {
        await context.Response.WriteTextAsync("partial", context.RequestAborted);
        throw new InvalidOperationException("Too late for a new response.");
    }

    [Get("slow")]
    public async ValueTask<IActionResult> SlowAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken);
        return EmptyResult.Instance;
    }

    [Get("query")]
    public ValueTask<IActionResult> QueryAsync(RequestContext context) => TextAsync(context.Request.QueryValue<int>("number").ToString(CultureInfo.InvariantCulture));

    [Get("files/{**path}", Name = "files")]
    public ValueTask<IActionResult> FilesAsync(RequestContext context) => TextAsync(context.RouteValues["path"]);

    [Get("optional/{page:int=1}", Name = "optional")]
    public ValueTask<IActionResult> OptionalAsync(RequestContext context) => TextAsync(RouteValue<int>("page").ToString(CultureInfo.InvariantCulture));
}

public sealed class CustomResult : IActionResult
{
    public ValueTask ExecuteAsync(RequestContext context)
    {
        var probe = context.Services.GetRequiredService<ScopedProbe>();
        context.Response.Headers["X-Result-Probe"] = probe.Id.ToString();
        return context.Response.WriteTextAsync("custom result", context.RequestAborted);
    }
}

public sealed class TraceAttribute(string value, int priority = 0) : MiddlewareAttribute(priority)
{
    public override ValueTask InvokeAsync(RequestContext context, RequestDelegate next)
    {
        if (!context.Items.TryGetValue("trace", out var existing))
        {
            context.Items["trace"] = existing = new List<string>();
        }

        ((List<string>)existing!).Add(value);
        return next(context);
    }
}

public sealed class ScopedServiceAttribute : MiddlewareAttribute
{
    public override ValueTask InvokeAsync(RequestContext context, RequestDelegate next)
    {
        var probe = context.Services.GetRequiredService<ScopedProbe>();
        context.Response.Headers["X-Attribute-Probe"] = probe.Id.ToString();
        return next(context);
    }
}

public sealed class ScopedProbe : IAsyncDisposable
{
    public Guid Id       { get; } = Guid.NewGuid();
    public bool Disposed { get; private set; }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}

public sealed class InjectedMiddleware(ScopedProbe probe, string header, int value) : IMiddleware
{
    public ValueTask InvokeAsync(RequestContext context, RequestDelegate next)
    {
        Assert.Same(probe, context.Services.GetRequiredService<ScopedProbe>());
        context.Response.Headers[header]       = probe.Id.ToString();
        context.Response.Headers["X-Argument"] = value.ToString(CultureInfo.InvariantCulture);
        context.Items["probe"]                 = probe;
        return next(context);
    }
}
