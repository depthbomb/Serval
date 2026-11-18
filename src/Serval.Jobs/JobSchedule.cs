#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Caprine.CronTimer;

namespace Serval.Jobs;

/// <summary>An immutable periodic or cron schedule, validated before registration.</summary>
public sealed class JobSchedule
{
    /// <summary>The fixed interval, or null for a cron schedule.</summary>
    public TimeSpan? Period { get; }

    /// <summary>The parsed cron expression, or null for a periodic schedule.</summary>
    public CronExpression? Expression { get; }

    /// <summary>The cron time zone, or null for a periodic schedule.</summary>
    public TimeZoneInfo? TimeZone { get; }

    private JobSchedule(TimeSpan period)
    {
        Period = period;
    }

    private JobSchedule(CronExpression expression, TimeZoneInfo timeZone)
    {
        Expression = expression;
        TimeZone   = timeZone;
    }

    /// <summary>Uses PeriodicTimer with an interval from one millisecond through 4,294,967,294 milliseconds.</summary>
    /// <remarks>The first execution waits one interval. Missed ticks coalesce according to PeriodicTimer semantics.</remarks>
    public static JobSchedule Every(TimeSpan period)
    {
        if (period.TotalMilliseconds is < 1 or > 4_294_967_294)
        {
            throw new ArgumentOutOfRangeException(nameof(period), "The interval must be supported by PeriodicTimer.");
        }

        return new JobSchedule(period);
    }

    /// <summary>Parses a cron expression for CronTimer. The default time zone is UTC.</summary>
    public static JobSchedule Cron(string                expression,
                                   TimeZoneInfo?         timeZone = null,
                                   CronExpressionOptions options  = CronExpressionOptions.None)
        => Cron(new CronExpression(expression, options), timeZone);

    /// <summary>Uses an already parsed or source-generated cron expression. The default time zone is UTC.</summary>
    public static JobSchedule Cron(CronExpression expression, TimeZoneInfo? timeZone = null)
    {
        ArgumentNullException.ThrowIfNull(expression);
        return new JobSchedule(expression, timeZone ?? TimeZoneInfo.Utc);
    }
}
