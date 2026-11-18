#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Caprine.CronTimer;
using Caprine.Durations;

namespace Serval.Sample.Jobs;

internal static partial class Schedules
{
    [GeneratedDuration("5s", DurationParseOptions.Strict)]
    internal static partial TimeSpan PulseInterval { get; }

    [GeneratedCronExpression("*/15 * * * * *", CronExpressionOptions.IncludeSeconds)]
    internal static partial CronExpression CalendarExpression { get; }
}
