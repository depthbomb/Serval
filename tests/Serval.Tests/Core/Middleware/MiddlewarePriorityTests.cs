#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.Extensions.DependencyInjection;
using System.Globalization;
using Serval.Hosting;
using Serval.Http;
using Serval.Http.Constants;
using Serval.Middleware;
using Serval.Tests.Support;

namespace Serval.Tests.Core.Middleware;

public sealed class MiddlewarePriorityTests
{
    [Fact]
    public async Task GeneratedRoutesCombineControllerAndActionPrioritiesStably()
    {
        await using var app      = new TestApplication();
        var             response = await app.SendAsync("/test/middleware-priority");
        Assert.Equal(StatusCodes.Ok, response.StatusCode);
        Assert.Equal("first,controller,controller-tie,default,default-tie,last", response.Text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitRoutesSortSignedPrioritiesStablyAndUnwindInReverse(bool fail)
    {
        var events = new List<string>();
        await using var app = new SecurityTestApplication(builder =>
        {
            builder.Services.AddSingleton(events);
            builder.AddController<object>(HttpMethods.Get, "/", null,
                (_, _) => ValueTask.FromResult<IActionResult>(new RecordingResult(events, fail)),
                new RecordingAttribute("max", int.MaxValue),
                new RecordingAttribute("positive", 10),
                new RecordingAttribute("default"),
                new RecordingAttribute("min", 20) { Priority = int.MinValue },
                new RecordingAttribute("negative", -10),
                new UseMiddlewareAttribute(typeof(RecordingMiddleware), "injected", 42) { Priority = 0 });
        });
        await using var request = app.Request();
        await app.Services.GetRequiredService<IRequestHandler>().HandleAsync(request.Context.Request, request.Response, CancellationToken.None);

        Assert.Equal(fail ? StatusCodes.InternalServerError : StatusCodes.Ok, request.Response.StatusCode);
        Assert.Equal(fail ? null : "42", request.Response.Headers["X-Argument"]);
        Assert.Equal([
            "min enter", "negative enter", "default enter", "injected enter", "positive enter", "max enter",
            "result",
            "max exit", "positive exit", "injected exit", "default exit", "negative exit", "min exit",
        ], events);
    }

    private sealed class RecordingAttribute(string name, int priority = 0) : MiddlewareAttribute(priority)
    {
        public override async ValueTask InvokeAsync(RequestContext context, RequestDelegate next)
        {
            var events = context.Services.GetRequiredService<List<string>>();
            events.Add(name + " enter");
            try
            {
                await next(context);
            }
            finally
            {
                events.Add(name + " exit");
            }
        }
    }

    public sealed class RecordingMiddleware(List<string> events, string name, int argument) : IMiddleware
    {
        public async ValueTask InvokeAsync(RequestContext context, RequestDelegate next)
        {
            context.Response.Headers["X-Argument"] = argument.ToString(CultureInfo.InvariantCulture);
            events.Add(name + " enter");
            try
            {
                await next(context);
            }
            finally
            {
                events.Add(name + " exit");
            }
        }
    }

    private sealed class RecordingResult(List<string> events, bool fail) : IActionResult
    {
        public ValueTask ExecuteAsync(RequestContext context)
        {
            events.Add("result");
            if (fail)
            {
                throw new InvalidOperationException("Result failed.");
            }

            return context.Response.WriteTextAsync("ok", context.RequestAborted);
        }
    }
}
