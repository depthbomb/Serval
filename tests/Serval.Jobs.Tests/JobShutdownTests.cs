#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;

namespace Serval.Jobs.Tests;

public sealed class JobShutdownTests
{
    [Fact]
    public async Task TimerDisposalFailureStillDrainsTheExecutionAndItsScope()
    {
        var state = new ExecutionState();
        var clock = new FaultingTimeProvider
        {
            FailDisposal = true,
        };
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton(state);
        services.AddSingleton<TimeProvider>(clock);
        services.AddScoped<ExecutionScope>();
        JobRegistration.Register(services, JobRegistration.Create<WaitingJob>(JobSchedule.Every(TimeSpan.FromDays(1)), "waiting"));
        var provider = services.BuildServiceProvider();
        try
        {
            var hosted   = provider.GetServices<IHostedService>().Last();
            var registry = provider.GetRequiredService<JobRegistry>();
            await hosted.StartAsync(CancellationToken.None);
            var run = registry.RunAsync("waiting");
            await state.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var stop = hosted.StopAsync(CancellationToken.None);
            await clock.DisposalAttempted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var observation = new CancellationTokenSource();
            await Task.WhenAny(stop, Task.Delay(100, observation.Token));
            await observation.CancelAsync();
            var stoppedEarly = stop.IsCompleted;
            state.Release.TrySetResult();
            await run;
            await Assert.ThrowsAsync<IOException>(() => stop);
            Assert.False(stoppedEarly, "Shutdown returned while a job still owned its scope.");
            Assert.Equal(1, state.Disposed);
            Assert.False(registry.GetJob("waiting").IsRunning);
        }
        finally
        {
            state.Release.TrySetResult();
            try
            {
                await provider.DisposeAsync();
            }
            catch (IOException) {}
        }
    }

    [Fact]
    public async Task PartialStartupFailureDisposesEarlierTimersAndRejectsManualRuns()
    {
        var clock = new FaultingTimeProvider
        {
            FailCreationAt = 2,
        };
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton<TimeProvider>(clock);
        JobRegistration.Register(services,
            JobRegistration.Create<JobRegistrationTests.EmptyJob>(JobSchedule.Every(TimeSpan.FromSeconds(1)), "one"),
            JobRegistration.Create<JobRegistrationTests.EmptyJob>(JobSchedule.Cron("* * * * *"), "two"));
        await using var provider = services.BuildServiceProvider();
        var             hosted   = provider.GetServices<IHostedService>().Last();
        var             registry = provider.GetRequiredService<JobRegistry>();
        await Assert.ThrowsAsync<IOException>(() => hosted.StartAsync(CancellationToken.None));
        Assert.Equal(1, clock.Disposed);
        Assert.All(registry.GetJobs(), job => Assert.False(job.IsScheduled));
        await Assert.ThrowsAsync<InvalidOperationException>(() => registry.RunAsync("one"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => registry.RunAsync("two"));
    }

    public sealed class ExecutionState
    {
        public int                  Disposed;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public sealed class ExecutionScope : IAsyncDisposable
    {
        private readonly ExecutionState _state;

        public ExecutionScope(ExecutionState state)
        {
            _state = state;
        }

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _state.Disposed);
            return ValueTask.CompletedTask;
        }
    }

    public sealed class WaitingJob : IJob
    {
        private readonly ExecutionState _state;
        private readonly ExecutionScope _scope;

        public WaitingJob(ExecutionState state, ExecutionScope scope)
        {
            _state = state;
            _scope = scope;
        }

        public async ValueTask ExecuteAsync(CancellationToken ct)
        {
            _state.Started.TrySetResult();
            await _state.Release.Task;
            GC.KeepAlive(_scope);
        }
    }

    private sealed class FaultingTimeProvider : TimeProvider
    {
        public           int                  Disposed;
        public           bool                 FailDisposal      { get; init; }
        public           int                  FailCreationAt    { get; init; }
        public           TaskCompletionSource DisposalAttempted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private          int                  _created;
        private readonly FakeTimeProvider     _clock = new();

        public override DateTimeOffset GetUtcNow() => _clock.GetUtcNow();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            => Interlocked.Increment(ref _created) == FailCreationAt ? throw new IOException("Timer creation failed.") : new FaultingTimer(_clock.CreateTimer(callback, state, dueTime, period), this);
    }

    private sealed class FaultingTimer : ITimer
    {
        private          int                  _disposed;
        private readonly ITimer               _inner;
        private readonly FaultingTimeProvider _owner;

        public FaultingTimer(ITimer inner, FaultingTimeProvider owner)
        {
            _inner = inner;
            _owner = owner;
        }

        public bool Change(TimeSpan dueTime, TimeSpan period) => _inner.Change(dueTime, period);

        public void Dispose()
        {
            _inner.Dispose();
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }
            Interlocked.Increment(ref _owner.Disposed);
            _owner.DisposalAttempted.TrySetResult();
            if (_owner.FailDisposal)
            {
                throw new IOException("Timer disposal failed.");
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
