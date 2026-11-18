#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

namespace Serval.Jobs;

/// <summary>An immutable snapshot of one registration's execution state.</summary>
public sealed record JobStatus
{
    /// <summary>The case-sensitive registration name.</summary>
    public required string Name { get; init; }

    /// <summary>The scoped class implementing the job.</summary>
    public required Type JobType { get; init; }

    /// <summary>The immutable schedule.</summary>
    public required JobSchedule Schedule { get; init; }

    /// <summary>Whether scheduled runs are paused. Manual runs remain available while the host is running.</summary>
    public bool IsPaused { get; init; }

    /// <summary>Whether an execution, including scope disposal, is in progress.</summary>
    public bool IsRunning { get; init; }

    /// <summary>Whether the timer loop is active, including while paused. False before startup, after shutdown, or after cron exhaustion.</summary>
    public bool IsScheduled { get; init; }

    /// <summary>A timer failure that stopped scheduling, or null. Execution failures are reported separately.</summary>
    public string? ScheduleError { get; init; }

    /// <summary>The UTC start time of the latest execution, or null before the first run.</summary>
    public DateTimeOffset? LastStartedAt { get; init; }

    /// <summary>The UTC completion time of the latest finished execution, including failures and cancellations.</summary>
    public DateTimeOffset? LastCompletedAt { get; init; }

    /// <summary>The latest completed execution's error message, or null after success or cooperative cancellation.</summary>
    public string? LastError { get; init; }

    /// <summary>The number of executions started, including failures and cancellations.</summary>
    public long RunCount { get; init; }

    /// <summary>The number of failed executions, excluding cooperative cancellation.</summary>
    public long FailureCount { get; init; }
}
