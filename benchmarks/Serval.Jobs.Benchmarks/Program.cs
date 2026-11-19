#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Caprine.CronTimer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Serval.Jobs.Benchmarks;

public static class Program
{
    private const int Samples = 7;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public static async Task Main(string[] args)
    {
        var measurements = new List<Measurement>();
        await using (var fixture = new Fixture(64, false))
        {
            await fixture.Host.StartAsync();
            measurements.Add(await MeasureAsync("get-job/idle", 1_000_000, count =>
            {
                for (var index = 0; index < count; index++)
                {
                    if (fixture.Registry.GetJob("job-0").RunCount != 0)
                    {
                        throw new InvalidOperationException("An idle job ran.");
                    }
                }
                return Task.CompletedTask;
            }));
            measurements.Add(await MeasureAsync("get-jobs/64-idle", 20_000, count =>
            {
                for (var index = 0; index < count; index++)
                {
                    if (fixture.Registry.GetJobs().Count != 64)
                    {
                        throw new InvalidOperationException("The job list changed.");
                    }
                }
                return Task.CompletedTask;
            }));
            foreach (var cancellable in new[] { false, true })
            {
                using var cts = new CancellationTokenSource();
                var       ct  = cancellable ? cts.Token : CancellationToken.None;
                measurements.Add(await MeasureAsync(cancellable ? "manual/cancellable" : "manual/no-token", 20_000, async count =>
                {
                    var before = fixture.State.Completed;
                    for (var index = 0; index < count; index++)
                    {
                        if (!await fixture.Registry.RunAsync("job-0", ct))
                        {
                            throw new InvalidOperationException("A sequential run was rejected.");
                        }
                    }
                    if (fixture.State.Completed - before != count)
                    {
                        throw new InvalidOperationException("An execution or disposal was lost.");
                    }
                }));
            }
        }

        foreach (var cron in new[] { false, true })
        {
            await using var fixture = new Fixture(1, true, cron);
            await fixture.Host.StartAsync();
            measurements.Add(await MeasureAsync(cron ? "scheduled/cron" : "scheduled/periodic", 20_000, async count =>
            {
                var before = fixture.State.Completed;
                for (var index = 0; index < count; index++)
                {
                    fixture.Clock.Advance(TimeSpan.FromSeconds(1));
                    if (!await fixture.State.Signal.WaitAsync(TimeSpan.FromSeconds(10)))
                    {
                        throw new TimeoutException("A scheduled execution was lost.");
                    }
                }
                if (fixture.State.Completed - before != count)
                {
                    throw new InvalidOperationException("An execution or disposal was lost.");
                }
            }));
        }

        var json = JsonSerializer.Serialize(new
        {
            Runtime = RuntimeInformation.FrameworkDescription,
            OS      = RuntimeInformation.OSDescription,
            Environment.ProcessorCount,
            Samples,
            Measurements = measurements,
        }, JsonOptions);
        Console.WriteLine(json);
        if (args.Length != 0)
        {
            await File.WriteAllTextAsync(args[0], json);
        }
    }

    private static async Task<Measurement> MeasureAsync(string name, int operations, Func<int, Task> run)
    {
        await run(Math.Max(1_000, operations / 10));
        var timings     = new double[Samples];
        var allocations = new double[Samples];
        for (var sample = 0; sample < Samples; sample++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            var allocated = GC.GetTotalAllocatedBytes(true);
            var started   = Stopwatch.GetTimestamp();
            await run(operations);
            timings[sample]     = Stopwatch.GetElapsedTime(started).TotalNanoseconds / operations;
            allocations[sample] = (GC.GetTotalAllocatedBytes(true) - allocated)      / (double)operations;
        }
        return new Measurement(name, operations, timings.Order().ElementAt(Samples / 2), allocations.Order().ElementAt(Samples / 2), timings, allocations);
    }

    private sealed record Measurement(string Name, int Operations, double NanosecondsPerOperation, double BytesPerOperation, double[] TimingSamples, double[] AllocationSamples);

    private sealed class Fixture : IAsyncDisposable
    {
        public IHost            Host     { get; }
        public JobRegistry      Registry { get; }
        public CompletionState  State    { get; }
        public FakeTimeProvider Clock    { get; } = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

        public Fixture(int jobs, bool scheduled, bool cron = false)
        {
            State = new CompletionState(scheduled);
            var host = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
            host.Logging.ClearProviders();
            host.Services.AddSingleton<TimeProvider>(Clock);
            host.Services.AddSingleton(State);
            host.Services.AddScoped<CompletionScope>();
            var registrations = new JobRegistration[jobs];
            for (var index = 0; index < jobs; index++)
            {
                registrations[index] = JobRegistration.Create<ProbeJob>(cron ? JobSchedule.Cron("* * * * * *", options: CronExpressionOptions.IncludeSeconds) : JobSchedule.Every(TimeSpan.FromSeconds(1)), $"job-{index}");
            }
            JobRegistration.Register(host.Services, registrations);
            Host     = host.Build();
            Registry = Host.Services.GetRequiredService<JobRegistry>();
        }

        public async ValueTask DisposeAsync()
        {
            await Host.StopAsync();
            await ((IAsyncDisposable)Host).DisposeAsync();
            State.Signal.Dispose();
        }
    }

    public sealed class CompletionState
    {
        public long          Completed;
        public bool          Scheduled { get; }
        public SemaphoreSlim Signal    { get; } = new(0);

        public CompletionState(bool scheduled)
        {
            Scheduled = scheduled;
        }
    }

    public sealed class CompletionScope : IAsyncDisposable
    {
        private readonly CompletionState _state;

        public CompletionScope(CompletionState state)
        {
            _state = state;
        }

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _state.Completed);
            if (_state.Scheduled)
            {
                _state.Signal.Release();
            }
            return ValueTask.CompletedTask;
        }
    }

    public sealed class ProbeJob : IJob
    {
        private readonly CompletionScope _scope;

        public ProbeJob(CompletionScope scope)
        {
            _scope = scope;
        }

        public ValueTask ExecuteAsync(CancellationToken ct)
        {
            GC.KeepAlive(_scope);
            return ValueTask.CompletedTask;
        }
    }
}
