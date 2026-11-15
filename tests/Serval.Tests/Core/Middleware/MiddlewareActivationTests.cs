#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.Extensions.DependencyInjection;
using System.Globalization;
using Serval.Middleware;
using Serval.Tests.Support;

namespace Serval.Tests.Core.Middleware;

public sealed class MiddlewareActivationTests
{
    [Fact]
    public async Task CachedFactoriesKeepRequestScopedServicesAndOptionalArguments()
    {
        await using var app        = new SecurityTestApplication(builder => builder.Services.AddScoped<ScopedProbe>());
        var             middleware = new UseMiddlewareAttribute(typeof(OptionalMiddleware));
        await using var first      = app.Request();
        await using var second     = app.Request();
        await middleware.InvokeAsync(first.Context, _ => ValueTask.CompletedTask);
        await middleware.InvokeAsync(second.Context, _ => ValueTask.CompletedTask);
        Assert.Equal("7", first.Response.Headers["X-Number"]);
        Assert.NotEqual(first.Response.Headers["X-Probe"], second.Response.Headers["X-Probe"]);
        Assert.Equal(first.Context.Services.GetRequiredService<ScopedProbe>().Id.ToString(), first.Response.Headers["X-Probe"]);
    }

    [Theory]
    [InlineData(false, "short")]
    [InlineData(true, "long")]
    public async Task MultipleConstructorsStillUseTheServicesAvailableInEachProvider(bool extraService, string expected)
    {
        await using var app = new SecurityTestApplication(builder =>
        {
            builder.Services.AddScoped<ScopedProbe>();
            if (extraService)
            {
                builder.Services.AddSingleton<ExtraService>();
            }
        });
        await using var request = app.Request();
        await new UseMiddlewareAttribute(typeof(MultipleConstructorsMiddleware)).InvokeAsync(request.Context, _ => ValueTask.CompletedTask);
        Assert.Equal(expected, request.Response.Headers["X-Constructor"]);
    }

    [Fact]
    public async Task NullArgumentsStillFailAndChangedArgumentTypesRemainSupported()
    {
        await using var app     = new SecurityTestApplication(_ => { });
        await using var request = app.Request();
        // ActivatorUtilities cannot infer the constructor argument type from null.
        await Assert.ThrowsAsync<InvalidOperationException>(() => new UseMiddlewareAttribute(typeof(ValueMiddleware), [null!])
                                                                  .InvokeAsync(request.Context, _ => ValueTask.CompletedTask).AsTask());
        object[] arguments  = [12];
        var      middleware = new UseMiddlewareAttribute(typeof(ValueMiddleware), arguments);
        await middleware.InvokeAsync(request.Context, _ => ValueTask.CompletedTask);
        Assert.Equal("12", request.Response.Headers["X-Value"]);
        arguments[0] = "changed";
        await middleware.InvokeAsync(request.Context, _ => ValueTask.CompletedTask);
        Assert.Equal("changed", request.Response.Headers["X-Value"]);
    }

    public sealed class ExtraService;

    public sealed class OptionalMiddleware(ScopedProbe probe, int number = 7) : IMiddleware
    {
        public ValueTask InvokeAsync(RequestContext context, RequestDelegate next)
        {
            context.Response.Headers["X-Probe"]  = probe.Id.ToString();
            context.Response.Headers["X-Number"] = number.ToString(CultureInfo.InvariantCulture);
            return next(context);
        }
    }

    public sealed class MultipleConstructorsMiddleware : IMiddleware
    {
        private readonly string _selected;

        public MultipleConstructorsMiddleware(ScopedProbe probe)
        {
            _selected = "short";
        }

        public MultipleConstructorsMiddleware(ScopedProbe probe, ExtraService extra)
        {
            _selected = "long";
        }

        public ValueTask InvokeAsync(RequestContext context, RequestDelegate next)
        {
            context.Response.Headers["X-Constructor"] = _selected;
            return next(context);
        }
    }

    public sealed class ValueMiddleware(object? value) : IMiddleware
    {
        public ValueTask InvokeAsync(RequestContext context, RequestDelegate next)
        {
            context.Response.Headers["X-Value"] = value?.ToString() ?? "null";
            return next(context);
        }
    }
}
