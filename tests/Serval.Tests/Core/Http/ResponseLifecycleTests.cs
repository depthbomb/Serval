#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Serval.Http;
using Serval.Http.Constants;
using Serval.Tests.Support;

namespace Serval.Tests.Core.Http;

public sealed class ResponseLifecycleTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartingCallbacksCannotFlushTheTransport(bool lifecycle)
    {
        var response = new MemoryResponse();
        if (lifecycle)
        {
            response.LifecycleStarting = ct => response.FlushAsync(ct);
        }
        else
        {
            response.OnStarting(ct => response.FlushAsync(ct));
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => response.StartAsync().AsTask());
        Assert.False(response.HasStarted);
        Assert.Empty(response.Text);
    }

    [Fact]
    public async Task EarlyHintsDoNotFireStartingHooksButAnUpgradeDoesOnce()
    {
        var events = new List<string>();
        var response = new UpgradeResponse();
        response.OnStarting(ct =>
        {
            events.Add("local");
            return ValueTask.CompletedTask;
        });
        response.LifecycleStarting = ct =>
        {
            events.Add("lifecycle");
            response.Headers["X-Upgrade"] = "yes";
            return ValueTask.CompletedTask;
        };
        Assert.True(await response.SendEarlyHintsAsync(new Headers { [HeaderNames.Link] = "</site.css>; rel=preload" }));
        Assert.Empty(events);
        await response.UpgradeAsync();
        Assert.Equal(["local", "lifecycle"], events);
        Assert.Equal("yes", response.Headers["X-Upgrade"]);
        await response.CompleteAsync();
        Assert.Equal(2, events.Count);
    }

    [Fact]
    public async Task DeferredResourcesDisposeInReverseOrderEvenWhenOneFails()
    {
        var events = new List<string>();
        await using var app = new TestApplication(configureBuilder: builder => builder.Use((context, next) =>
        {
            context.Response.RegisterForDisposeAsync(new Resource(events, "outer"));
            context.Response.RegisterForDisposeAsync(new Resource(events, "inner", true));
            context.Response.OnCompleted(() =>
            {
                events.Add("completed");
                return ValueTask.CompletedTask;
            });
            return next(context);
        }));
        Assert.Equal(200, (await app.SendAsync("/test/task")).StatusCode);
        Assert.Equal(["completed", "inner", "outer"], events);
    }

    [Fact]
    public async Task CompletionCallbacksKeepTheirBudgetWithoutLifecycleHandlers()
    {
        var laterCanceled = false;
        await using var app = new TestApplication(
            options => options.CompletionTimeout = TimeSpan.FromMilliseconds(40),
            configureBuilder: builder => builder.Use((context, next) =>
            {
                context.Response.OnCompleted(ct =>
                {
                    laterCanceled = ct.IsCancellationRequested;
                    return ValueTask.CompletedTask;
                });
                context.Response.OnCompleted(async ct => await Task.Delay(Timeout.Infinite, ct));
                return next(context);
            }));

        Assert.Equal(200, (await app.SendAsync("/test/task").WaitAsync(TimeSpan.FromSeconds(5))).StatusCode);
        Assert.True(laterCanceled);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EndedResponsesRejectWritesAndLateStartupCallbacks(bool abort)
    {
        var response = new MemoryResponse();
        if (abort)
        {
            await response.AbortAsync();
        }
        else
        {
            await response.CompleteAsync();
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => response.WriteTextAsync("late").AsTask());
        Assert.Throws<InvalidOperationException>(() => response.OnStarting(() => ValueTask.CompletedTask));
        Assert.Throws<InvalidOperationException>(() => response.RegisterForDisposeAsync(new Resource([], "late")));
    }

    private sealed class Resource : IAsyncDisposable
    {
        private readonly List<string> _events;
        private readonly string _name;
        private readonly bool _fail;

        public Resource(List<string> events, string name, bool fail = false)
        {
            _events = events;
            _name = name;
            _fail = fail;
        }

        public async ValueTask DisposeAsync()
        {
            await Task.Yield();
            _events.Add(_name);
            if (_fail)
            {
                throw new InvalidOperationException("cleanup");
            }
        }
    }

    private sealed class UpgradeResponse : Response
    {
        public override bool SupportsEarlyHints => true;
        public ValueTask UpgradeAsync() => MarkUpgradedAsync();
        public override ValueTask FlushAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
        public override async ValueTask CompleteAsync(CancellationToken ct = default)
        {
            await StartAsync(ct);
            MarkCompleted();
        }

        public override ValueTask AbortAsync(CancellationToken ct = default)
        {
            MarkAborted();
            return ValueTask.CompletedTask;
        }

        protected override ValueTask OnStartAsync(CancellationToken ct) => ValueTask.CompletedTask;
        protected override ValueTask OnWriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken ct) => ValueTask.CompletedTask;
        protected override ValueTask OnSendEarlyHintsAsync(Headers headers, CancellationToken ct) => ValueTask.CompletedTask;
    }
}
