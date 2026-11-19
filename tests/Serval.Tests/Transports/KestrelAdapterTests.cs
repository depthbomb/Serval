#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using System.IO.Pipelines;
using System.Net.WebSockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using Serval.Hosting;
using Serval.Transport.Kestrel;
using Request = Serval.Http.Request;
using Response = Serval.Http.Response;

namespace Serval.Tests.Transports;

public sealed class KestrelAdapterTests
{
    [Fact]
    public async Task RequestHeadersPreserveSeparateValuesAndFreezeTheirSnapshot()
    {
        var context = CreateContext();
        for (var index = 0; index < 12; index++)
        {
            context.Request.Headers[$"X-Client-{index}"] = $"value-{index}";
        }

        var values = new[] { "one,two", "three" };
        context.Request.Headers["X-Multiple"] = new StringValues(values);
        context.Request.Headers["X-Empty"]    = string.Empty;
        await InvokeAsync(context, (request, _, _) =>
        {
            values[0]                             = "changed";
            context.Request.Headers["X-Client-0"] = "changed";
            Assert.Equal("value-0", request.Headers["x-client-0"]);
            Assert.Equal("value-11", request.Headers["X-Client-11"]);
            Assert.Equal(["one,two", "three"], request.Headers.GetValues("x-multiple"));
            Assert.Equal(string.Empty, Assert.Single(request.Headers.GetValues("X-Empty")));
            Assert.Throws<InvalidOperationException>(() => request.Headers.Append("X-New", "value"));
            Assert.Throws<InvalidOperationException>(() => request.Headers["X-Client-0"] = "changed");
            return ValueTask.CompletedTask;
        });
    }

    [Fact]
    public async Task CompletionRejectsCanceledTokenAfterHeadersHaveStarted()
    {
        var context = CreateContext();
        var feature = new PendingTransportFeature();
        context.Features.Set<IHttpResponseBodyFeature>(feature);
        context.Features.Set<IHttpRequestLifetimeFeature>(feature);
        using var cts = new CancellationTokenSource();
        await InvokeAsync(context, async (_, response, _) =>
        {
            await response.StartAsync(CancellationToken.None);
            await cts.CancelAsync();
            var completion = response.CompleteAsync(cts.Token).AsTask();
            try
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => completion.WaitAsync(TimeSpan.FromSeconds(1), CancellationToken.None));
                Assert.Equal(0, feature.Completions);
            }
            finally
            {
                feature.Release();
                _ = await Record.ExceptionAsync(() => completion);
            }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationAbortsPendingNativeCompletionOrUpgrade(bool upgrade)
    {
        var context = CreateContext();
        var feature = new PendingTransportFeature();
        context.Features.Set<IHttpResponseBodyFeature>(feature);
        context.Features.Set<IHttpRequestLifetimeFeature>(feature);
        context.Features.Set<IHttpWebSocketFeature>(feature);
        using var cts = new CancellationTokenSource();
        await InvokeAsync(context, async (_, response, _) =>
        {
            var pending = upgrade
                ? response.AcceptWebSocketAsync(null, cts.Token).AsTask()
                : response.CompleteAsync(cts.Token).AsTask();
            try
            {
                Assert.False(pending.IsCompleted);
                await cts.CancelAsync();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(1), CancellationToken.None));
                Assert.True(feature.Aborted);
            }
            finally
            {
                feature.Release();
                _ = await Record.ExceptionAsync(() => pending);
            }
        });
    }

    private static DefaultHttpContext CreateContext()
        => new()
        {
            Request =
            {
                Protocol = "HTTP/1.1",
                Method   = "GET",
                Scheme   = "http",
                Host     = new HostString("localhost"),
                Path     = "/",
            },
        };

    private static async Task InvokeAsync(HttpContext context, Func<Request, Response, CancellationToken, ValueTask> callback)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddServal();
        builder.Services.AddSingleton<IRequestHandler>(new CallbackHandler(callback));
        using var host        = builder.Build();
        var       application = new ApplicationBuilder(host.Services);
        application.UseServal();
        await application.Build()(context);
    }

    private sealed class CallbackHandler : IRequestHandler
    {
        private readonly Func<Request, Response, CancellationToken, ValueTask> _callback;

        public CallbackHandler(Func<Request, Response, CancellationToken, ValueTask> callback)
        {
            _callback = callback;
        }

        public ValueTask HandleAsync(Request request, Response response, CancellationToken ct) => _callback(request, response, ct);
    }

    private sealed class PendingTransportFeature : IHttpResponseBodyFeature, IHttpRequestLifetimeFeature, IHttpWebSocketFeature
    {
        public Stream            Stream             => Stream.Null;
        public PipeWriter        Writer             { get; } = PipeWriter.Create(Stream.Null);
        public CancellationToken RequestAborted     { get; set; }
        public bool              IsWebSocketRequest => true;
        public bool              Aborted            { get; private set; }
        public int               Completions        { get; private set; }

        private readonly TaskCompletionSource            _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<WebSocket> _upgrade    = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void DisableBuffering() {}

        public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task SendFileAsync(string path, long offset, long? count, CancellationToken ct = default) => throw new NotSupportedException();

        public Task CompleteAsync()
        {
            Completions++;
            return _completion.Task;
        }

        public Task<WebSocket> AcceptAsync(WebSocketAcceptContext acceptContext) => _upgrade.Task;

        public void Abort()
        {
            Aborted = true;
            _completion.TrySetCanceled();
            _upgrade.TrySetCanceled();
        }

        public void Release()
        {
            _completion.TrySetResult();
            _upgrade.TrySetCanceled();
            Writer.Complete();
        }
    }
}
