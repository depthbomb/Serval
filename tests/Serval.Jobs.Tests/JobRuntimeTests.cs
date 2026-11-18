#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using System.Collections.Concurrent;
using Caprine.CronTimer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Serval.Jobs.Tests;

public sealed class JobRuntimeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TimersUseInjectedClockAndFreshAsyncScopes(bool cron)
    {
        await using var fixture = new Fixture(cron ? JobSchedule.Cron("* * * * * *", options: CronExpressionOptions.IncludeSeconds) : JobSchedule.Every(TimeSpan.FromSeconds(1)));
        await fixture.StartAsync();
        Assert.Empty(fixture.State.Instances);
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        await EventuallyAsync(() => fixture.Registry.GetJob("test").LastCompletedAt is not null);
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        await EventuallyAsync(() => fixture.State.Disposed == 2 && !fixture.Registry.GetJob("test").IsRunning);

        Assert.Equal(2, fixture.State.Instances.Distinct().Count());
        Assert.Equal(2, fixture.State.JobInstances.Distinct().Count());
        Assert.Equal(2, fixture.State.JobsDisposed);
        Assert.Equal(2, fixture.Registry.GetJob("test").RunCount);
        Assert.Equal(0, fixture.Registry.GetJob("test").FailureCount);
        Assert.Equal(fixture.Clock.GetUtcNow(), fixture.Registry.GetJob("test").LastCompletedAt);
    }

    [Fact]
    public async Task PauseKeepsDefinitionsAndAllowsManualRuns()
    {
        await using var fixture  = new Fixture(JobSchedule.Every(TimeSpan.FromSeconds(1)));
        var             snapshot = fixture.Registry.GetJobs();
        fixture.Registry.Pause("test");
        await fixture.StartAsync();
        fixture.Clock.Advance(TimeSpan.FromSeconds(10));
        await Task.Delay(40);
        Assert.Equal(0, fixture.Registry.GetJob("test").RunCount);
        Assert.True(fixture.Registry.GetJob("test").IsPaused);
        Assert.False(snapshot[0].IsPaused);
        Assert.True(await fixture.Registry.RunAsync("test"));
        fixture.Registry.Resume("test");
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        await EventuallyAsync(() => fixture.State.Disposed == 2);

        Assert.Single(fixture.Registry.GetJobs());
        Assert.Same(snapshot[0].Schedule, fixture.Registry.GetJob("test").Schedule);
    }

    [Fact]
    public async Task ConcurrentTriggersAndScheduledTicksDoNotOverlap()
    {
        await using var fixture = new Fixture(JobSchedule.Every(TimeSpan.FromSeconds(1)));
        var             entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var             release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.State.Work = async ct =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
        };
        await fixture.StartAsync();
        var first = fixture.Registry.RunAsync("test");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var rejected = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() => fixture.Registry.RunAsync("test"))));
        Assert.All(rejected, value => Assert.False(value));
        fixture.Clock.Advance(TimeSpan.FromSeconds(4));
        await Task.Delay(40);
        Assert.Equal(1, fixture.Registry.GetJob("test").RunCount);
        fixture.Registry.Pause("test");
        release.SetResult();
        Assert.True(await first);
        Assert.Equal(1, fixture.State.Disposed);
    }

    [Fact]
    public async Task SlowScheduledRunsCoalesceTicksWithoutOverlapping()
    {
        await using var fixture = new Fixture(JobSchedule.Every(TimeSpan.FromSeconds(1)));
        var             entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var             release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.State.Work = async ct =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
        };
        await fixture.StartAsync();
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(1, fixture.Registry.GetJob("test").RunCount);
        release.SetResult();
        await EventuallyAsync(() => fixture.State.Disposed == 2);
        Assert.Equal(2, fixture.Registry.GetJob("test").RunCount);
    }

    [Fact]
    public async Task ScheduledFailureIsRecordedAndLaterTicksContinue()
    {
        await using var fixture = new Fixture(JobSchedule.Every(TimeSpan.FromSeconds(1)));
        fixture.State.Work = _ => throw new InvalidOperationException("job failed");
        await fixture.StartAsync();
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        await EventuallyAsync(() => fixture.Registry.GetJob("test").FailureCount == 1);
        Assert.Equal("job failed", fixture.Registry.GetJob("test").LastError);
        fixture.State.Work = _ => ValueTask.CompletedTask;
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        await EventuallyAsync(() => fixture.State.Disposed == 2 && !fixture.Registry.GetJob("test").IsRunning);
        Assert.Null(fixture.Registry.GetJob("test").LastError);
        Assert.Equal(1, fixture.Registry.GetJob("test").FailureCount);
    }

    [Fact]
    public async Task ManualFailuresPropagateAndReleaseTheJobForAnotherRun()
    {
        await using var fixture = new Fixture(JobSchedule.Every(TimeSpan.FromDays(1)));
        fixture.State.Work = _ => throw new InvalidOperationException("manual failure");
        await fixture.StartAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Registry.RunAsync("test"));
        Assert.Equal(1, fixture.State.Disposed);
        fixture.State.Work = _ => ValueTask.CompletedTask;
        Assert.True(await fixture.Registry.RunAsync("test"));
        Assert.Equal(1, fixture.Registry.GetJob("test").FailureCount);
    }

    [Fact]
    public async Task ManualCancellationIsCooperativeAndDoesNotStopTheSchedule()
    {
        await using var fixture = new Fixture(JobSchedule.Every(TimeSpan.FromSeconds(1)));
        var             entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.State.Work = async ct =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        };
        await fixture.StartAsync();
        using var cts = new CancellationTokenSource();
        var       run = fixture.Registry.RunAsync("test", cts.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Equal(0, fixture.Registry.GetJob("test").FailureCount);
        fixture.State.Work = _ => ValueTask.CompletedTask;
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        await EventuallyAsync(() => fixture.State.Disposed == 2);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShutdownCancelsAndDrainsScheduledAndManualRuns(bool manual)
    {
        await using var fixture = new Fixture(JobSchedule.Every(TimeSpan.FromSeconds(1)));
        var             entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.State.Work = async ct =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        };
        await fixture.StartAsync();
        Task<bool>? run = null;
        if (manual)
        {
            run = fixture.Registry.RunAsync("test");
        }
        else
        {
            fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        }

        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.Host.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        if (run is not null)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        }

        Assert.Equal(1, fixture.State.Disposed);
        Assert.False(fixture.Registry.GetJob("test").IsRunning);
        Assert.Equal(0, fixture.Registry.GetJob("test").FailureCount);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Registry.RunAsync("test"));
    }

    [Fact]
    public async Task DifferentJobsRunIndependently()
    {
        await using var fixture     = new Fixture(JobSchedule.Every(TimeSpan.FromDays(1)), jobs => jobs.Add(JobRegistration.Create<ProbeJob>(JobSchedule.Every(TimeSpan.FromDays(1)), "other")));
        var             bothEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var             release     = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var             count       = 0;
        fixture.State.Work = async ct =>
        {
            if (Interlocked.Increment(ref count) == 2)
            {
                bothEntered.SetResult();
            }
            await release.Task.WaitAsync(ct);
        };
        await fixture.StartAsync();
        var first  = fixture.Registry.RunAsync("test");
        var second = fixture.Registry.RunAsync("other");
        await bothEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        release.SetResult();
        Assert.All(await Task.WhenAll(first, second), value => Assert.True(value));
    }

    [Fact]
    public async Task SynchronousScheduledWorkDoesNotHoldTheRegistryControlLock()
    {
        await using var fixture = new Fixture(JobSchedule.Every(TimeSpan.FromSeconds(1)));
        var             entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var       release = new ManualResetEventSlim();
        fixture.State.Work = ct =>
        {
            entered.TrySetResult();
            release.Wait(ct);
            return ValueTask.CompletedTask;
        };
        try
        {
            await fixture.StartAsync();
            await Task.Run(() => fixture.Clock.Advance(TimeSpan.FromSeconds(1))).WaitAsync(TimeSpan.FromSeconds(5));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Run(() => fixture.Registry.Pause("test")).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(fixture.Registry.GetJob("test").IsRunning);
            Assert.False(await fixture.Registry.RunAsync("test"));
            await fixture.Host.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, fixture.State.Disposed);
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public async Task CronUsesTheConfiguredTimeZone()
    {
        var             zone    = TimeZoneInfo.CreateCustomTimeZone("test+2", TimeSpan.FromHours(2), "test+2", "test+2");
        await using var fixture = new Fixture(JobSchedule.Cron("1 2 * * *", zone));
        await fixture.StartAsync();
        fixture.Clock.Advance(TimeSpan.FromSeconds(59));
        await Task.Delay(40);
        Assert.Equal(0, fixture.Registry.GetJob("test").RunCount);
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        await EventuallyAsync(() => fixture.State.Disposed == 1);
    }

    [Fact]
    public async Task ExhaustedCronCanStillBeTriggeredManually()
    {
        await using var fixture = new Fixture(JobSchedule.Cron("0 0 1 1 * 2020", options: CronExpressionOptions.IncludeYear));
        await fixture.StartAsync();
        fixture.Clock.Advance(TimeSpan.FromDays(2));
        await EventuallyAsync(() => !fixture.Registry.GetJob("test").IsScheduled);
        Assert.True(await fixture.Registry.RunAsync("test"));
        Assert.Equal(1, fixture.State.Disposed);
    }

    [Fact]
    public async Task AsyncScopeDisposalRetainsOverlapProtectionAndShutdownWaitsForIt()
    {
        await using var fixture   = new Fixture(JobSchedule.Every(TimeSpan.FromDays(1)));
        var             disposing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var             release   = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.State.DisposeWork = async () =>
        {
            disposing.TrySetResult();
            await release.Task;
        };
        await fixture.StartAsync();
        var run = fixture.Registry.RunAsync("test");
        try
        {
            await disposing.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(fixture.Registry.GetJob("test").IsRunning);
            Assert.False(await fixture.Registry.RunAsync("test"));
            var stop = fixture.Host.StopAsync();
            Assert.False(stop.IsCompleted);
            release.SetResult();
            Assert.True(await run);
            await stop.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, fixture.State.Disposed);
            Assert.False(fixture.Registry.GetJob("test").IsScheduled);
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [Fact]
    public async Task DisposalFailureIsRecordedAndDoesNotLeaveTheJobRunning()
    {
        await using var fixture = new Fixture(JobSchedule.Every(TimeSpan.FromDays(1)));
        fixture.State.DisposeWork = () => throw new InvalidOperationException("dispose failed");
        await fixture.StartAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Registry.RunAsync("test"));
        Assert.False(fixture.Registry.GetJob("test").IsRunning);
        Assert.Equal("dispose failed", fixture.Registry.GetJob("test").LastError);
        fixture.State.DisposeWork = () => ValueTask.CompletedTask;
        Assert.True(await fixture.Registry.RunAsync("test"));
    }

    [Fact]
    public async Task ShutdownDeadlineStopsAdmissionWhileTheJobFinishesCooperatively()
    {
        await using var fixture = new Fixture(JobSchedule.Every(TimeSpan.FromDays(1)));
        var             entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var             release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.State.Work = async _ =>
        {
            entered.TrySetResult();
            await release.Task;
        };
        await fixture.StartAsync();
        var run = fixture.Registry.RunAsync("test");
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Host.StopAsync(cts.Token));
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Registry.RunAsync("test"));
            Assert.True(fixture.Registry.GetJob("test").IsRunning);
        }
        finally
        {
            release.TrySetResult();
            await run;
        }

        await fixture.Host.StopAsync();
        Assert.Equal(1, fixture.State.Disposed);
    }

    [Fact]
    public async Task ControlsRejectUnknownNamesAndRunsBeforeStartup()
    {
        await using var fixture = new Fixture(JobSchedule.Every(TimeSpan.FromDays(1)));
        Assert.Throws<KeyNotFoundException>(() => fixture.Registry.GetJob("TEST"));
        Assert.Throws<KeyNotFoundException>(() => fixture.Registry.Pause("missing"));
        Assert.Throws<KeyNotFoundException>(() => fixture.Registry.Resume("missing"));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => fixture.Registry.RunAsync("missing"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Registry.RunAsync("test"));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await fixture.StartAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Registry.RunAsync("test", cts.Token));
        Assert.Equal(0, fixture.Registry.GetJob("test").RunCount);
    }

    private static async Task EventuallyAsync(Func<bool> condition)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            await Task.Delay(1, cts.Token);
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public IHost            Host     { get; }
        public JobRegistry      Registry => Host.Services.GetRequiredService<JobRegistry>();
        public FakeTimeProvider Clock    { get; } = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        public ProbeState       State    { get; } = new();

        public Fixture(JobSchedule schedule, Action<List<JobRegistration>>? extra = null)
        {
            var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
            builder.Logging.ClearProviders();
            builder.Services.AddSingleton<TimeProvider>(Clock);
            builder.Services.AddSingleton(State);
            builder.Services.AddScoped<ProbeDependency>();
            var jobs = new List<JobRegistration>
            {
                JobRegistration.Create<ProbeJob>(schedule, "test")
            };
            extra?.Invoke(jobs);
            JobRegistration.Register(builder.Services, [.. jobs]);
            Host = builder.Build();
        }

        public Task StartAsync() => Host.StartAsync();

        public async ValueTask DisposeAsync()
        {
            await Host.StopAsync();
            await ((IAsyncDisposable)Host).DisposeAsync();
        }
    }

    public sealed class ProbeState
    {
        public int                                Disposed;
        public int                                JobsDisposed;
        public ConcurrentQueue<Guid>              Instances    { get; }      = new();
        public ConcurrentQueue<Guid>              JobInstances { get; }      = new();
        public Func<CancellationToken, ValueTask> Work         { get; set; } = _ => ValueTask.CompletedTask;
        public Func<ValueTask>                    DisposeWork  { get; set; } = () => ValueTask.CompletedTask;
    }

    public sealed class ProbeDependency : IAsyncDisposable
    {
        public           Guid       Id { get; } = Guid.NewGuid();
        private readonly ProbeState _state;

        public ProbeDependency(ProbeState state) => _state = state;

        public async ValueTask DisposeAsync()
        {
            await Task.Yield();
            await _state.DisposeWork();
            Interlocked.Increment(ref _state.Disposed);
        }
    }

    public sealed class ProbeJob : IJob, IAsyncDisposable
    {
        private readonly ProbeDependency _dependency;
        private readonly ProbeState      _state;
        private readonly Guid            _id = Guid.NewGuid();

        public ProbeJob(ProbeDependency dependency, ProbeState state, JobRegistry registry)
        {
            _dependency = dependency;
            _state      = state;
            Assert.NotNull(registry);
        }

        public ValueTask ExecuteAsync(CancellationToken ct)
        {
            _state.Instances.Enqueue(_dependency.Id);
            _state.JobInstances.Enqueue(_id);
            return _state.Work(ct);
        }

        public async ValueTask DisposeAsync()
        {
            await Task.Yield();
            Interlocked.Increment(ref _state.JobsDisposed);
        }
    }
}
