#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using System.Collections.Frozen;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serval.Jobs.Internal;

namespace Serval.Jobs;

/// <summary>A singleton registry of fixed job definitions with thread-safe execution controls.</summary>
/// <remarks>
///     Definitions cannot be added, removed, or rescheduled. Execution state lives in this process only.
///     Multiple application instances each run their own schedules; this registry does not coordinate across hosts.
/// </remarks>
public sealed class JobRegistry : IAsyncDisposable
{
    private readonly JobRunner[]                         _runners;
    private readonly FrozenDictionary<string, JobRunner> _byName;

    internal JobRegistry(JobDefinition[] definitions, IServiceScopeFactory scopes, TimeProvider timeProvider, ILoggerFactory loggers)
    {
        var logger = loggers.CreateLogger<JobRegistry>();
        _runners = [.. definitions.Select(definition => new JobRunner(definition, scopes, timeProvider, logger))];
        _byName  = _runners.ToFrozenDictionary(runner => runner.GetStatus().Name, StringComparer.Ordinal);
    }

    /// <summary>Stops scheduling, cancels executions, and awaits their async scope disposal.</summary>
    /// <remarks>Normally called by the DI container. Jobs must cooperate with cancellation.</remarks>
    public async ValueTask DisposeAsync() => await StopAsync(CancellationToken.None).ConfigureAwait(false);

    /// <summary>Returns independent snapshots in registration order. Snapshots across jobs are not atomic.</summary>
    public IReadOnlyList<JobStatus> GetJobs()
    {
        var snapshots = new JobStatus[_runners.Length];
        for (var index = 0; index < _runners.Length; index++)
        {
            snapshots[index] = _runners[index].GetStatus();
        }

        return Array.AsReadOnly(snapshots);
    }

    /// <summary>Returns a snapshot for a case-sensitive name. Unknown names throw KeyNotFoundException.</summary>
    public JobStatus GetJob(string name) => Find(name).GetStatus();

    /// <summary>Prevents new scheduled runs. An admitted execution continues, and manual runs remain available.</summary>
    public void Pause(string name) => Find(name).SetPaused(true);

    /// <summary>Allows scheduled runs on the original cadence. Does not trigger an execution by itself.</summary>
    /// <remarks>A timer tick coalesced during an in-flight scheduled run may be consumed after that run finishes.</remarks>
    public void Resume(string name) => Find(name).SetPaused(false);

    /// <summary>Runs a job now and awaits execution and scope disposal, or returns false if it is already running.</summary>
    /// <remarks>
    ///     Allowed only after host startup and before shutdown, including while the schedule is paused.
    ///     Exceptions propagate to the caller. Cancellation cancels this execution cooperatively.
    ///     Requests are never queued. A scheduled tick arriving during a manual run is skipped.
    /// </remarks>
    public Task<bool> RunAsync(string name, CancellationToken ct = default) => Find(name).RunAsync(false, ct);

    internal async Task StartAsync(CancellationToken ct)
    {
        try
        {
            foreach (var runner in _runners)
            {
                ct.ThrowIfCancellationRequested();
                runner.Start();
            }
        }
        catch
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    internal Task StopAsync(CancellationToken ct)
    {
        var completion = Task.WhenAll(_runners.Select(runner => runner.StopAsync()));
        return completion.WaitAsync(ct);
    }

    private JobRunner Find(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return _byName.TryGetValue(name, out var runner) ? runner : throw new KeyNotFoundException($"Job '{name}' is not registered.");
    }
}
