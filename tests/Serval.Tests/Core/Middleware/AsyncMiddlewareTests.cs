#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.Extensions.DependencyInjection;
using Serval.Hosting;
using Serval.Http;
using Serval.Http.Results;
using Serval.Middleware;
using Serval.Tests.Support;

namespace Serval.Tests.Core.Middleware;

public sealed class AsyncMiddlewareTests
{
    [Fact]
    public void SynchronousOnlyCleanupIsRejectedBeforeMiddlewareIsConstructed()
    {
        var error = Assert.Throws<ArgumentException>(() => new UseMiddlewareAttribute(typeof(SynchronousCleanupMiddleware)));
        Assert.Equal("middlewareType", error.ParamName);
        Assert.Contains(nameof(IAsyncDisposable), error.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RequestsAwaitMiddlewareCleanupAfterWritingSuccessfulOrErrorResponses(bool fail)
    {
        var lifetime = new PendingCleanup();
        await using var app = new SecurityTestApplication(builder =>
        {
            builder.Services.AddSingleton(lifetime);
            builder.AddController<object>("GET", "/", null, (_, _) => fail
                    ? ValueTask.FromException<IActionResult>(new InvalidOperationException("Action failed."))
                    : ValueTask.FromResult<IActionResult>(new TextResult("done")),
                new UseMiddlewareAttribute(typeof(PendingCleanupMiddleware)));
        });
        await using var request = app.Request();
        var             pending = app.Services.GetRequiredService<IRequestHandler>().HandleAsync(request.Context.Request, request.Response, CancellationToken.None).AsTask();
        try
        {
            await lifetime.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(pending.IsCompleted);
            Assert.False(lifetime.Disposed);
            Assert.Equal(fail ? 500 : 200, request.Response.StatusCode);
            Assert.NotEmpty(request.Response.Text);
        }
        finally
        {
            lifetime.Release.TrySetResult();
            await pending.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.True(lifetime.Disposed);
        Assert.Equal(fail ? 500 : 200, request.Response.StatusCode);
    }

    public sealed class PendingCleanup
    {
        public TaskCompletionSource Entered  { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release  { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool                 Disposed { get; set; }
    }

    public sealed class PendingCleanupMiddleware(PendingCleanup lifetime) : IMiddleware, IAsyncDisposable, IDisposable
    {
        public async ValueTask DisposeAsync()
        {
            lifetime.Entered.TrySetResult();
            await lifetime.Release.Task;
            lifetime.Disposed = true;
        }

        public void Dispose() => throw new InvalidOperationException("Synchronous cleanup must not run.");

        public ValueTask InvokeAsync(RequestContext context, RequestDelegate next) => next(context);
    }

    public sealed class SynchronousCleanupMiddleware : IMiddleware, IDisposable
    {
        public SynchronousCleanupMiddleware()
        {
            throw new InvalidOperationException("Registration must fail before construction.");
        }

        public void Dispose() => throw new InvalidOperationException("Synchronous cleanup must not run.");

        public ValueTask InvokeAsync(RequestContext context, RequestDelegate next) => next(context);
    }
}
