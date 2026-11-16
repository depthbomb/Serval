#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Serval.Hosting;
using Serval.Http;
using Serval.Http.Results;
using Serval.Http.StaticFiles;
using Serval.Lifecycle;
using Serval.Middleware;
using Serval.Routing;

namespace Serval.Tests.Core.Hosting;

public sealed class RequestScopeTests
{
    [Fact]
    public async Task ScopeCreationFailuresReachTheCentralHandlerWithoutResolvingRootScopedServices()
    {
        var calls = 0;
        await using var services = Application((_, _) => ValueTask.FromResult<IActionResult>(new TextResult("unreachable")), options => options.TraceIdGenerator = () =>
        {
            calls++;
            return "scope-failure";
        });
        var failure  = new InvalidOperationException("Scope creation failed.");
        var factory  = new TestScopeFactory(() => throw failure);
        var errors   = new RecordingExceptionHandler();
        var logger   = new RecordingLogger();
        var response = new RecordingResponse();
        var handler  = Handler(services, factory, errors, logger);

        await handler.HandleAsync(Request(), response, CancellationToken.None);

        Assert.Same(failure, Assert.Single(errors.Exceptions));
        Assert.Equal(502, response.StatusCode);
        Assert.Equal("central handler", response.Text);
        Assert.Equal(1, response.Completions);
        Assert.False(response.Aborted);
        Assert.NotNull(errors.Context);
        Assert.Equal("scope-failure", errors.Context.TraceId);
        Assert.Equal(1, calls);
        Assert.Throws<InvalidOperationException>(() => errors.Context.Services.GetService(typeof(ScopedDependency)));
        Assert.Empty(logger.Exceptions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CleanupFailuresAreReportedAfterSuccessfulResponsesWithoutRewritingThem(bool empty)
    {
        await using var services = Application((_, _) => empty
            ? ValueTask.FromResult<IActionResult>(new StatusCodeResult(204))
            : ValueTask.FromResult<IActionResult>(new TextResult("success")));
        var failure  = new InvalidOperationException("Cleanup failed.");
        var scope    = new FailingDisposalScope(services.CreateScope(), failure);
        var errors   = new RecordingExceptionHandler();
        var logger   = new RecordingLogger();
        var response = new RecordingResponse();
        var handler  = Handler(services, new TestScopeFactory(() => scope), errors, logger);

        await handler.HandleAsync(Request(), response, CancellationToken.None);

        Assert.Equal(1, scope.Disposals);
        Assert.True(scope.InnerDisposed);
        Assert.Equal(empty ? 204 : 200, response.StatusCode);
        Assert.Equal(empty ? "" : "success", response.Text);
        Assert.Equal(1, response.Completions);
        Assert.False(response.Aborted);
        Assert.Empty(errors.Exceptions);
        Assert.Same(failure, Assert.Single(logger.Exceptions));
        Assert.Contains(response.Headers["X-Test-Trace"]!, Assert.Single(logger.Messages));
    }

    [Fact]
    public async Task CleanupFailuresDoNotReplaceTheOriginalApplicationError()
    {
        var             original = new HttpErrorException(409, "An intentional conflict.");
        await using var services = Application((_, _) => throw original);
        var             cleanup  = new InvalidOperationException("Cleanup failed.");
        var             scope    = new FailingDisposalScope(services.CreateScope(), cleanup);
        var             errors   = new RecordingExceptionHandler();
        var             logger   = new RecordingLogger();
        var             response = new RecordingResponse();
        var             handler  = Handler(services, new TestScopeFactory(() => scope), errors, logger);

        await handler.HandleAsync(Request(), response, CancellationToken.None);

        Assert.Same(original, Assert.Single(errors.Exceptions));
        Assert.Same(cleanup, Assert.Single(logger.Exceptions));
        Assert.Contains(errors.Context!.TraceId, Assert.Single(logger.Messages));
        Assert.Equal("central handler", response.Text);
        Assert.Equal(1, response.Completions);
    }

    [Fact]
    public async Task CleanupFailuresDoNotMaskTransportExceptions()
    {
        var             original = new RequestTransportException("The stream was reset.");
        await using var services = Application((_, _) => throw original);
        var             cleanup  = new InvalidOperationException("Cleanup failed.");
        var             scope    = new FailingDisposalScope(services.CreateScope(), cleanup);
        var             errors   = new RecordingExceptionHandler();
        var             logger   = new RecordingLogger();
        var             response = new RecordingResponse();
        var             handler  = Handler(services, new TestScopeFactory(() => scope), errors, logger);

        var thrown = await Assert.ThrowsAsync<RequestTransportException>(() => handler.HandleAsync(Request(), response, CancellationToken.None).AsTask());

        Assert.Same(original, thrown);
        Assert.Same(cleanup, Assert.Single(logger.Exceptions));
        Assert.Empty(errors.Exceptions);
        Assert.Equal(1, scope.Disposals);
        Assert.Equal(0, response.Completions);
    }

    [Fact]
    public async Task CleanupFailuresDoNotReplaceClientCancellation()
    {
        await using var services = Application((_, context) =>
        {
            context.RequestAborted.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IActionResult>(new TextResult("unreachable"));
        });
        var       cleanup      = new InvalidOperationException("Cleanup failed.");
        var       scope        = new FailingDisposalScope(services.CreateScope(), cleanup);
        var       errors       = new RecordingExceptionHandler();
        var       logger       = new RecordingLogger();
        var       response     = new RecordingResponse();
        var       handler      = Handler(services, new TestScopeFactory(() => scope), errors, logger);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await handler.HandleAsync(Request(), response, cancellation.Token);

        Assert.True(response.Aborted);
        Assert.Equal(0, response.Completions);
        Assert.Empty(errors.Exceptions);
        Assert.Same(cleanup, Assert.Single(logger.Exceptions));
    }

    [Fact]
    public async Task ExceptionHandlerAndCleanupFailuresAreBothReported()
    {
        var             original  = new InvalidOperationException("Action failed.");
        await using var services  = Application((_, _) => throw original);
        var             cleanup   = new InvalidOperationException("Cleanup failed.");
        var             rendering = new InvalidOperationException("Error rendering failed.");
        var             scope     = new FailingDisposalScope(services.CreateScope(), cleanup);
        var             errors    = new RecordingExceptionHandler { Failure = rendering };
        var             logger    = new RecordingLogger();
        var             response  = new RecordingResponse();
        var             handler   = Handler(services, new TestScopeFactory(() => scope), errors, logger);

        await handler.HandleAsync(Request(), response, CancellationToken.None);

        Assert.Same(original, Assert.Single(errors.Exceptions));
        Assert.Equal([rendering, cleanup], logger.Exceptions);
        Assert.True(response.Aborted);
        Assert.Equal(0, response.Completions);
    }

    [Fact]
    public async Task ActualAsyncServiceDisposalFailuresAreContained()
    {
        await using var services = Application((_, context) =>
        {
            context.Services.GetRequiredService<ThrowingDependency>();
            return ValueTask.FromResult<IActionResult>(new TextResult("success"));
        });
        var errors   = new RecordingExceptionHandler();
        var logger   = new RecordingLogger();
        var response = new RecordingResponse();
        var handler  = Handler(services, services.GetRequiredService<IServiceScopeFactory>(), errors, logger);

        await handler.HandleAsync(Request(), response, CancellationToken.None);

        Assert.Equal("success", response.Text);
        Assert.Equal(1, response.Completions);
        Assert.Equal("Async disposal failed.", Assert.Single(logger.Exceptions)!.Message);
        Assert.Empty(errors.Exceptions);
    }

    private static ServiceProvider Application(ControllerAction<object> action, Action<ServalOptions>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<ScopedDependency>();
        services.AddScoped<ThrowingDependency>();
        services.AddServal(configure).AddController<object>("GET", "/", null, (controller, context) =>
        {
            context.Response.Headers["X-Test-Trace"] = context.TraceId;
            return action(controller, context);
        });
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    private static RequestHandler Handler(ServiceProvider services, IServiceScopeFactory factory, IRequestExceptionHandler errors, ILogger<RequestHandler> logger)
        => new(new ScopeProvider(factory), services.GetRequiredService<Router>(), services.GetRequiredService<StaticFileServer>(), errors,
            services.GetRequiredService<ServalOptions>(), services.GetRequiredService<ApplicationPipeline>(), services.GetRequiredService<RequestLifecycleRegistry>(), logger);

    private static Request Request() => new("GET", new Uri("http://localhost/"), HttpVersion.Version11, new Headers(), Stream.Null, 1024);

    private sealed class ScopedDependency;

    private sealed class ThrowingDependency : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Task.Yield();
            throw new InvalidOperationException("Async disposal failed.");
        }
    }

    private sealed class ScopeProvider(IServiceScopeFactory factory) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType == typeof(IServiceScopeFactory) ? factory : null;
    }

    private sealed class TestScopeFactory(Func<IServiceScope> create) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => create();
    }

    private sealed class FailingDisposalScope(IServiceScope inner, Exception failure) : IServiceScope, IAsyncDisposable
    {
        public int  Disposals     { get; private set; }
        public bool InnerDisposed { get; private set; }

        public IServiceProvider ServiceProvider => inner.ServiceProvider;

        public async ValueTask DisposeAsync()
        {
            Disposals++;
            await new AsyncServiceScope(inner).DisposeAsync();
            InnerDisposed = true;
            throw failure;
        }

        public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private sealed class RecordingExceptionHandler : IRequestExceptionHandler
    {
        public List<Exception> Exceptions { get; } = [];
        public RequestContext? Context    { get; private set; }
        public Exception?      Failure    { get; init; }

        public ValueTask HandleAsync(RequestContext context, Exception exception)
        {
            Context = context;
            Exceptions.Add(exception);
            if (Failure is not null)
            {
                throw Failure;
            }

            context.Response.StatusCode = 502;
            return context.Response.WriteTextAsync("central handler");
        }
    }

    private sealed class RecordingLogger : ILogger<RequestHandler>
    {
        public ConcurrentQueue<Exception?> Exceptions { get; } = new();
        public ConcurrentQueue<string>     Messages   { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Exceptions.Enqueue(exception);
            Messages.Enqueue(formatter(state, exception));
        }
    }

    private sealed class RecordingResponse : Response
    {
        public           int          Completions { get; private set; }
        public           bool         Aborted     { get; private set; }
        public           string       Text        => Encoding.UTF8.GetString(_body.ToArray());
        private readonly MemoryStream _body = new();

        public override ValueTask FlushAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public override ValueTask CompleteAsync(CancellationToken cancellationToken = default)
        {
            Completions++;
            return StartAsync(cancellationToken);
        }

        public override ValueTask AbortAsync(CancellationToken cancellationToken = default)
        {
            Aborted = true;
            return ValueTask.CompletedTask;
        }

        protected override ValueTask OnStartAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        protected override ValueTask OnWriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken) => _body.WriteAsync(bytes, cancellationToken);
    }
}
