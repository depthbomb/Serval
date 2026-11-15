#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.Extensions.DependencyInjection;
using Serval.Http;
using Serval.Middleware;

namespace Serval.Tests.Support;

public sealed class MiddlewareLifetime
{
    public List<string> Events { get; } = [];
    public ScopedProbe? Probe  { get; set; }
}

public sealed class LifetimeResult(bool fail) : IActionResult
{
    public async ValueTask ExecuteAsync(RequestContext context)
    {
        var lifetime = context.Services.GetRequiredService<MiddlewareLifetime>();
        Assert.Same(lifetime.Probe, context.Services.GetRequiredService<ScopedProbe>());
        Assert.False(lifetime.Probe!.Disposed);
        lifetime.Events.Add("result");
        if (fail)
        {
            throw new InvalidOperationException("Result execution failed.");
        }

        await context.Response.WriteTextAsync("completed", context.RequestAborted);
    }
}

public sealed class OuterLifetimeMiddleware(MiddlewareLifetime lifetime, ScopedProbe probe) : IMiddleware, IAsyncDisposable
{
    public async ValueTask DisposeAsync()
    {
        await Task.Yield();
        Assert.False(probe.Disposed);
        lifetime.Events.Add("outer dispose");
    }

    public async ValueTask InvokeAsync(RequestContext context, RequestDelegate next)
    {
        lifetime.Probe = probe;
        lifetime.Events.Add("outer enter");
        await next(context);
        Assert.True(context.Response.HasStarted);
        lifetime.Events.Add("outer exit");
    }
}

public sealed class AsyncLifetimeMiddleware(MiddlewareLifetime lifetime, ScopedProbe probe) : IMiddleware, IAsyncDisposable, IDisposable
{
    public async ValueTask DisposeAsync()
    {
        await Task.Yield();
        Assert.False(probe.Disposed);
        lifetime.Events.Add("async dispose");
    }

    public void Dispose() => throw new InvalidOperationException("Async disposal should take precedence.");

    public async ValueTask InvokeAsync(RequestContext context, RequestDelegate next)
    {
        lifetime.Events.Add("async enter");
        await next(context);
        Assert.True(context.Response.HasStarted);
        lifetime.Events.Add("async exit");
    }
}
