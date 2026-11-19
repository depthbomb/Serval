#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using Serval.Hosting;
using Serval.Lifecycle;
using Serval.Middleware;
using Serval.Tests.Support;

namespace Serval.Tests.Core.Hosting;

public sealed class RequestLifecycleTests
{
    [Fact]
    public async Task PrioritiesAreStableInEveryPhaseAndLocalCallbacksRunFirst()
    {
        var probe = new Probe();
        await using var app = new TestApplication(configureBuilder: builder =>
        {
            builder.Services.AddSingleton(probe);
            builder.AddRequestLifecycleHandler<Recorder<Last>>(10);
            builder.AddRequestLifecycleHandler<Recorder<First>>(-10);
            builder.AddRequestLifecycleHandler<Recorder<Middle>>();
            builder.AddRequestLifecycleHandler<Recorder<Tie>>();
            builder.Use(async (context, next) =>
            {
                probe.Events.Enqueue("middleware enter");
                context.Response.OnStarting(() => Record(probe, "local outer start"));
                context.Response.OnStarting(() => Record(probe, "local inner start"));
                context.Response.OnCompleted(() => Record(probe, "local outer complete"));
                context.Response.OnCompleted(() => Record(probe, "local inner complete"));
                await next(context);
                probe.Events.Enqueue("middleware exit");
            });
        });

        var response = await app.SendAsync("/test/task");
        Assert.Equal(200, response.StatusCode);
        Assert.Equal(["First", "Middle", "Tie", "Last"], response.Headers.GetValues("X-Lifecycle"));
        Assert.Equal([
            "First received", "Middle received", "Tie received", "Last received", "middleware enter",
            "local inner start", "local outer start", "First start", "Middle start", "Tie start", "Last start",
            "middleware exit", "local inner complete", "local outer complete",
            "First complete", "Middle complete", "Tie complete", "Last complete",
            "Last dispose", "Tie dispose", "Middle dispose", "First dispose",
        ], probe.Events);
        Assert.All(probe.Results, result => Assert.False(result.ScopeDisposed));
        Assert.All(probe.Results, result => Assert.True(result.Scope.Disposed));
    }

    [Theory]
    [InlineData("/test/task", "GET", 200, RequestCompletionReason.Completed, false)]
    [InlineData("/test/task", "HEAD", 200, RequestCompletionReason.Completed, false)]
    [InlineData("/test/task", "OPTIONS", 204, RequestCompletionReason.Completed, false)]
    [InlineData("/test/task", "POST", 405, RequestCompletionReason.Completed, false)]
    [InlineData("/missing", "GET", 404, RequestCompletionReason.Completed, false)]
    [InlineData("/test/crash", "GET", 500, RequestCompletionReason.Completed, false)]
    [InlineData("/test/partial", "GET", 200, RequestCompletionReason.Aborted, true)]
    public async Task HooksCoverDispatchErrorsAndAborts(string path, string method, int status, RequestCompletionReason reason, bool aborted)
    {
        var             probe    = new Probe();
        await using var app      = Application(probe);
        var             response = await app.SendAsync(path, method);
        var             result   = Assert.Single(probe.Results);
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(status, result.Completion.StatusCode);
        Assert.Equal(reason, result.Completion.Reason);
        Assert.Equal(aborted, result.Completion.IsAborted);
        Assert.True(result.Completion.HasStarted);
        Assert.True(result.Completion.Elapsed > TimeSpan.Zero);
        Assert.Equal(["First"], response.Headers.GetValues("X-Lifecycle"));
        Assert.Equal(["First received", "First start", "First complete", "First dispose"], probe.Events);
        if (path is "/test/crash" or "/test/partial")
        {
            Assert.IsType<InvalidOperationException>(result.Completion.Exception);
        }
    }

    [Fact]
    public async Task HooksCoverStaticFilesAndCanonicalRedirects()
    {
        var directory = Directory.CreateTempSubdirectory("serval-lifecycle-").FullName;
        await File.WriteAllTextAsync(Path.Combine(directory, "hello.txt"), "hello");
        var probe = new Probe();
        await using var app = Application(probe, options =>
        {
            options.StaticFiles.DirectoryPath = directory;
            options.TrailingSlashes           = TrailingSlashBehavior.RedirectToWithoutSlash;
        });
        Assert.Equal("hello", (await app.SendAsync("/hello.txt")).Text);
        Assert.Equal(308, (await app.SendAsync("/hello.txt/")).StatusCode);
        Assert.Equal(2, probe.Results.Count);
        Assert.All(probe.Results, result => Assert.Equal(RequestCompletionReason.Completed, result.Completion.Reason));
    }

    [Fact]
    public async Task EachConcurrentRequestGetsOneHandlerInstanceSharedWithItsServiceScope()
    {
        var probe          = new Probe();
        var instancesMatch = new ConcurrentBag<bool>();
        probe.Received = (_, context, _) =>
        {
            context.Items[typeof(Recorder<First>)] = context.Services.GetRequiredService<Recorder<First>>();
            return ValueTask.CompletedTask;
        };
        probe.Completed = (_, context, _, _) =>
        {
            instancesMatch.Add(ReferenceEquals(context.Items[typeof(Recorder<First>)], context.Services.GetRequiredService<Recorder<First>>()));
            return ValueTask.CompletedTask;
        };
        await using var app = Application(probe);
        await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => app.SendAsync("/test/task")));
        Assert.Equal(12, probe.Results.Select(result => result.Instance).Distinct().Count());
        Assert.Equal(12, instancesMatch.Count);
        Assert.All(instancesMatch, Assert.True);
        Assert.Equal(12, probe.Events.Count(item => item == "First dispose"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReceivingAndStartingFailuresDoNotRepeatHooksDuringErrorHandling(bool receiving)
    {
        var probe   = new Probe();
        var failure = new InvalidOperationException("hook failed");
        if (receiving)
        {
            probe.Received = (name, _, _) => name == "First" ? ValueTask.FromException(failure) : ValueTask.CompletedTask;
        }
        else
        {
            probe.Starting = (name, _, _) => name == "First" ? ValueTask.FromException(failure) : ValueTask.CompletedTask;
        }

        await using var app      = Application(probe, configureBuilder: builder => builder.AddRequestLifecycleHandler<Recorder<Last>>());
        var             response = await app.SendAsync("/test/task");
        Assert.Equal(500, response.StatusCode);
        Assert.Equal(1, probe.Events.Count(item => item == "First start"));
        Assert.Equal(1, probe.Events.Count(item => item == "Last start"));
        Assert.Equal(2, probe.Results.Count);
        Assert.All(probe.Results, result => Assert.Same(failure, result.Completion.Exception));
        Assert.Equal(receiving ? new[] { "First", "Last" } : new[] { "Last" }, response.Headers.GetValues("X-Lifecycle"));
    }

    [Fact]
    public async Task MultipleStartingFailuresAbortWithoutRepeatingAnyHook()
    {
        var probe = new Probe
        {
            Starting = (_, _, _) => ValueTask.FromException(new InvalidOperationException("startup")),
        };
        await using var app      = Application(probe, configureBuilder: builder => builder.AddRequestLifecycleHandler<Recorder<Last>>());
        var             response = await app.SendAsync("/test/task");
        Assert.True(response.Aborted);
        Assert.Equal(1, probe.Events.Count(item => item == "First start"));
        Assert.Equal(1, probe.Events.Count(item => item == "Last start"));
        Assert.All(probe.Results, result => Assert.True(result.Completion.IsAborted));
    }

    [Theory]
    [InlineData("write")]
    [InlineData("flush")]
    [InlineData("complete")]
    [InlineData("abort")]
    public async Task ReceivingHooksCannotSendResponses(string operation)
    {
        var probe = new Probe
        {
            Received = (_, context, ct) => operation switch
            {
                "flush"    => context.Response.FlushAsync(ct),
                "complete" => context.Response.CompleteAsync(ct),
                "abort"    => context.Response.AbortAsync(ct),
                _          => context.Response.WriteTextAsync("forbidden", ct),
            },
        };
        await using var app      = Application(probe);
        var             response = await app.SendAsync("/test/task");
        Assert.Equal(500, response.StatusCode);
        Assert.DoesNotContain("forbidden", response.Text);
        Assert.IsType<InvalidOperationException>(Assert.Single(probe.Results).Completion.Exception);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationAndTimeoutHaveDistinctOutcomesAndIndependentCompletionTokens(bool clientCancellation)
    {
        using var cts                = new CancellationTokenSource();
        var       completionCanceled = true;
        var probe = new Probe
        {
            Completed = (_, _, _, ct) =>
            {
                completionCanceled = ct.IsCancellationRequested;
                return ValueTask.CompletedTask;
            },
        };
        await using var app = Application(probe, options => options.RequestTimeout = clientCancellation ? TimeSpan.FromSeconds(10) : TimeSpan.FromMilliseconds(40));
        if (clientCancellation)
        {
            await cts.CancelAsync();
        }

        var response   = await app.SendAsync("/test/slow", cancellationToken: cts.Token);
        var completion = Assert.Single(probe.Results).Completion;
        Assert.Equal(clientCancellation ? RequestCompletionReason.Canceled : RequestCompletionReason.TimedOut, completion.Reason);
        Assert.Equal(clientCancellation, completion.IsAborted);
        Assert.False(completionCanceled);
        Assert.Equal(clientCancellation ? 200 : 408, response.StatusCode);
    }

    [Fact]
    public async Task CompletionBudgetCancelsCooperativeHooksButStillInvokesLaterHandlers()
    {
        var laterCanceled = false;
        var probe = new Probe
        {
            Completed = async (name, _, _, ct) =>
            {
                if (name == "First")
                {
                    await Task.Delay(Timeout.Infinite, ct);
                }
                else
                {
                    laterCanceled = ct.IsCancellationRequested;
                }
            },
        };
        await using var app = Application(probe, options => options.CompletionTimeout = TimeSpan.FromMilliseconds(40),
            builder => builder.AddRequestLifecycleHandler<Recorder<Last>>());
        Assert.Equal(200, (await app.SendAsync("/test/task").WaitAsync(TimeSpan.FromSeconds(5))).StatusCode);
        Assert.True(laterCanceled);
        Assert.Equal(2, probe.Events.Count(item => item.EndsWith(" dispose", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task MiddlewareResourcesSurviveCallbacksAndLifecycleCompletion()
    {
        var             probe = new Probe();
        await using var app   = Application(probe, configureBuilder: builder => builder.UseMiddleware<ResourceMiddleware>());
        await app.SendAsync("/test/crash");
        Assert.Equal([
            "First received", "resource start", "First start", "resource complete", "First complete",
            "resource dispose", "First dispose",
        ], probe.Events);
    }

    [Fact]
    public async Task CompletionFailuresAndFailingLoggersCannotSkipCleanup()
    {
        var probe = new Probe
        {
            Completed = (_, _, _, _) => ValueTask.FromException(new InvalidOperationException("completion")),
        };
        await using var app = Application(probe, configureBuilder: builder =>
        {
            builder.AddRequestLifecycleHandler<Recorder<Last>>();
            builder.UseMiddleware<ResourceMiddleware>();
            builder.Services.AddLogging(logging => logging.AddProvider(new ThrowingLogProvider()));
            builder.Use((context, next) =>
            {
                context.Response.OnCompleted(() => throw new InvalidOperationException("local completion"));
                return next(context);
            });
        });
        Assert.Equal(200, (await app.SendAsync("/test/task")).StatusCode);
        Assert.Contains("Last complete", probe.Events);
        Assert.Contains("resource dispose", probe.Events);
        Assert.Equal(2, probe.Events.Count(item => item is "First dispose" or "Last dispose"));
        Assert.All(probe.Results, result => Assert.True(result.Scope.Disposed));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailingLoggersCannotPreventTransportAbort(bool started)
    {
        var probe = new Probe();
        await using var app = Application(probe, configureBuilder: builder =>
        {
            builder.UseMiddleware<ResourceMiddleware>();
            builder.Services.AddLogging(logging => logging.AddProvider(new ThrowingLogProvider()));
        });

        var response = await app.SendAsync(started ? "/test/partial" : "/test/crash");
        Assert.True(response.Aborted);
        var completion = Assert.Single(probe.Results).Completion;
        Assert.True(completion.IsAborted);
        Assert.Equal(RequestCompletionReason.Aborted, completion.Reason);
        Assert.Contains("resource dispose", probe.Events);
        Assert.True(Assert.Single(probe.Results).Scope.Disposed);
    }

    [Fact]
    public async Task CompletionCallbacksCannotWriteOrChangeHeadersEvenWhenAbortedBeforeStartup()
    {
        var rejected = false;
        var probe = new Probe
        {
            Completed = async (_, context, _, _) =>
            {
                try
                {
                    await context.Response.WriteTextAsync("late", CancellationToken.None);
                }
                catch (InvalidOperationException)
                {
                    rejected = true;
                }
            },
        };
        await using var app = Application(probe);
        using var       cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var response = await app.SendAsync("/test/slow", cancellationToken: cts.Token);
        Assert.True(rejected);
        Assert.Empty(response.Text);
        Assert.Throws<InvalidOperationException>(() => response.Headers["X-Late"] = "no");
        Assert.Throws<InvalidOperationException>(() => response.StatusCode        = 201);
    }

    [Fact]
    public void RegistrationRejectsDuplicateNonScopedAndLateHandlers()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new Probe());
        services.AddScoped<ScopedProbe>();
        var builder = services.AddServal().AddRequestLifecycleHandler<Recorder<First>>();
        Assert.Throws<InvalidOperationException>(() => builder.AddRequestLifecycleHandler<Recorder<First>>());
        services.AddSingleton<Recorder<Last>>();
        Assert.Throws<InvalidOperationException>(() => builder.AddRequestLifecycleHandler<Recorder<Last>>());
        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IRequestHandler>();
        Assert.Throws<InvalidOperationException>(() => builder.AddRequestLifecycleHandler<Recorder<Middle>>());
    }

    [Fact]
    public async Task CompletionAwaitsHooksThatHaveNotObservedCancellationBeforeDisposingTheirScope()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probe = new Probe
        {
            Completed = async (_, _, _, ct) =>
            {
                await using var registration = ct.Register(() => entered.TrySetResult());
                await release.Task;
            },
        };
        await using var app     = Application(probe, options => options.CompletionTimeout = TimeSpan.FromMilliseconds(30));
        var             pending = app.SendAsync("/test/task");
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(pending.IsCompleted);
            Assert.False(Assert.Single(probe.Results).Scope.Disposed);
            Assert.DoesNotContain("First dispose", probe.Events);
        }
        finally
        {
            release.TrySetResult();
            await pending.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.True(Assert.Single(probe.Results).Scope.Disposed);
    }

    [Fact]
    public async Task ActivationFailureStillCompletesPreviouslyResolvedHandlers()
    {
        var             probe    = new Probe();
        await using var app      = Application(probe, configureBuilder: builder => builder.AddRequestLifecycleHandler<BrokenHandler>());
        var             response = await app.SendAsync("/test/task");
        Assert.Equal(500, response.StatusCode);
        Assert.Equal(["First start", "First complete", "First dispose"], probe.Events);
        Assert.Equal("activation failed", Assert.Single(probe.Results).Completion.Exception!.Message);
    }

    [Fact]
    public async Task TransportFailuresAreReportedToHooksAndRethrownToTheTransport()
    {
        var failure = new RequestTransportException("reset");
        var probe   = new Probe();
        await using var app = Application(probe, configureBuilder: builder => builder.AddController<object>("GET", "/reset", null,
            (_, _) => throw failure));
        Assert.Same(failure, await Assert.ThrowsAsync<RequestTransportException>(() => app.SendAsync("/reset")));
        var completion = Assert.Single(probe.Results).Completion;
        Assert.Same(failure, completion.Exception);
        Assert.Equal(RequestCompletionReason.TransportError, completion.Reason);
        Assert.True(completion.IsAborted);
        Assert.False(completion.HasStarted);
    }

    [Fact]
    public async Task GroupRegistrationIsApplicationWideAndServiceOverridesAreValidatedAtStartup()
    {
        var probe = new Probe();
        await using (var app = new TestApplication(configureBuilder: builder =>
                     {
                         builder.Services.AddSingleton(probe);
                         builder.AddGroup("/group", group => group.AddRequestLifecycleHandler<Recorder<First>>());
                     }))
        {
            Assert.Equal(404, (await app.SendAsync("/outside-group")).StatusCode);
            Assert.Single(probe.Results);
        }

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddServal().AddRequestLifecycleHandler<Recorder<First>>();
        services.AddSingleton<Recorder<First>>();
        await using var provider = services.BuildServiceProvider();
        var             error    = Assert.Throws<InvalidOperationException>(provider.GetRequiredService<IRequestHandler>);
        Assert.Contains("scoped", error.Message);
    }

    private static TestApplication Application(Probe probe, Action<ServalOptions>? configure = null, Action<ServalBuilder>? configureBuilder = null)
        => new(configure, configureBuilder: builder =>
        {
            builder.Services.AddSingleton(probe);
            builder.AddRequestLifecycleHandler<Recorder<First>>();
            configureBuilder?.Invoke(builder);
        });

    private static ValueTask Record(Probe probe, string message)
    {
        probe.Events.Enqueue(message);
        return ValueTask.CompletedTask;
    }

    public sealed class BrokenHandler : RequestLifecycleHandler
    {
        public BrokenHandler()
        {
            throw new InvalidOperationException("activation failed");
        }
    }

    public sealed class First;

    public sealed class Middle;

    public sealed class Tie;

    public sealed class Last;

    public sealed class Probe
    {
        public ConcurrentQueue<string>                                                                               Events    { get; } = new();
        public ConcurrentBag<(object Instance, RequestCompletion Completion, ScopedProbe Scope, bool ScopeDisposed)> Results   { get; } = [];
        public Func<string, RequestContext, CancellationToken, ValueTask>?                                           Received  { get; set; }
        public Func<string, RequestContext, CancellationToken, ValueTask>?                                           Starting  { get; set; }
        public Func<string, RequestContext, RequestCompletion, CancellationToken, ValueTask>?                        Completed { get; set; }
    }

    public sealed class Recorder<T> : RequestLifecycleHandler, IAsyncDisposable
    {
        private readonly Probe       _probe;
        private readonly ScopedProbe _scope;
        private readonly string      _name = typeof(T).Name;

        public Recorder(Probe probe, ScopedProbe scope)
        {
            _probe = probe;
            _scope = scope;
        }

        public override async ValueTask OnRequestReceivedAsync(RequestContext context, CancellationToken ct)
        {
            await Task.Yield();
            _probe.Events.Enqueue(_name + " received");
            if (_probe.Received is {} callback)
            {
                await callback(_name, context, ct);
            }
        }

        public override async ValueTask OnResponseStartingAsync(RequestContext context, CancellationToken ct)
        {
            _probe.Events.Enqueue(_name + " start");
            context.Response.Headers.Append("X-Lifecycle", _name);
            if (_probe.Starting is {} callback)
            {
                await callback(_name, context, ct);
            }
        }

        public override async ValueTask OnRequestCompletedAsync(RequestContext context, RequestCompletion completion, CancellationToken ct)
        {
            _probe.Events.Enqueue(_name + " complete");
            _probe.Results.Add((this, completion, _scope, _scope.Disposed));
            if (_probe.Completed is {} callback)
            {
                await callback(_name, context, completion, ct);
            }
        }

        public ValueTask DisposeAsync() => Record(_probe, _name + " dispose");
    }

    public sealed class ResourceMiddleware : IMiddleware, IAsyncDisposable
    {
        private          bool  _disposed;
        private readonly Probe _probe;

        public ResourceMiddleware(Probe probe)
        {
            _probe = probe;
        }

        public ValueTask InvokeAsync(RequestContext context, RequestDelegate next)
        {
            context.Response.OnStarting(() => Record(_probe, _disposed ? "disposed too soon" : "resource start"));
            context.Response.OnCompleted(() => Record(_probe, _disposed ? "disposed too soon" : "resource complete"));
            return next(context);
        }

        public ValueTask DisposeAsync()
        {
            _disposed = true;
            return Record(_probe, "resource dispose");
        }
    }

    private sealed class ThrowingLogProvider : ILoggerProvider, ILogger
    {
        public ILogger CreateLogger(string categoryName) => this;

        public void Dispose() {}

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => throw new InvalidOperationException("logging failed");
    }
}
