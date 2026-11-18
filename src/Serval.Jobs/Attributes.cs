#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Caprine.CronTimer;

namespace Serval.Jobs;

/// <summary>Includes a public IJob class in generated registration with a PeriodicTimer schedule.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class PeriodicJobAttribute : Attribute
{
    /// <summary>The literal interval in milliseconds, or zero when using a schedule member.</summary>
    public double Milliseconds { get; }

    /// <summary>The type declaring the schedule member, or null for a literal interval.</summary>
    public Type? ScheduleType { get; }

    /// <summary>The static TimeSpan property or parameterless method, or null for a literal interval.</summary>
    public string? MemberName { get; }

    /// <summary>A case-sensitive registry name. Defaults to the fully qualified job type name.</summary>
    public string? Name { get; init; }

    /// <summary>Declares an interval from one millisecond through 4,294,967,294 milliseconds.</summary>
    public PeriodicJobAttribute(double milliseconds)
    {
        Milliseconds = milliseconds;
    }

    /// <summary>Uses a static get-only property or parameterless method returning TimeSpan.</summary>
    /// <remarks>The member must be accessible to generated code. Its value is read and validated once during registration.</remarks>
    public PeriodicJobAttribute(Type scheduleType, string memberName)
    {
        ArgumentNullException.ThrowIfNull(scheduleType);
        ArgumentException.ThrowIfNullOrWhiteSpace(memberName);

        ScheduleType = scheduleType;
        MemberName   = memberName;
    }
}

/// <summary>Includes a public IJob class in generated registration with a CronTimer schedule.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class CronJobAttribute : Attribute
{
    /// <summary>The literal cron expression, or an empty string when using a schedule member.</summary>
    public string Expression { get; } = string.Empty;

    /// <summary>The type declaring the schedule member, or null for a literal expression.</summary>
    public Type? ScheduleType { get; }

    /// <summary>The static CronExpression property or parameterless method, or null for a literal expression.</summary>
    public string? MemberName { get; }

    /// <summary>A case-sensitive registry name. Defaults to the fully qualified job type name.</summary>
    public string? Name { get; init; }

    /// <summary>A system time-zone identifier, resolved during registration. Defaults to UTC.</summary>
    public string TimeZoneId { get; init; } = "UTC";

    /// <summary>Syntax extensions for literal expressions. For a schedule member, set options on that expression instead.</summary>
    public CronExpressionOptions Options { get; init; }

    /// <summary>Declares a cron schedule. Invalid syntax fails registration before the host starts.</summary>
    public CronJobAttribute(string expression)
    {
        Expression = expression;
    }

    /// <summary>Uses a static get-only property or parameterless method returning CronExpression.</summary>
    /// <remarks>The member must be accessible to generated code. Its value is read once during registration.</remarks>
    public CronJobAttribute(Type scheduleType, string memberName)
    {
        ArgumentNullException.ThrowIfNull(scheduleType);
        ArgumentException.ThrowIfNullOrWhiteSpace(memberName);

        ScheduleType = scheduleType;
        MemberName   = memberName;
    }
}
