#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.Extensions.Logging;
using Serval.Jobs;

namespace Serval.Sample.Jobs;

[CronJob(typeof(Schedules), nameof(Schedules.CalendarExpression), Name = "calendar")]
public sealed class CalendarJob : IJob
{
    private readonly ILogger<CalendarJob> _logger;

    public CalendarJob(ILogger<CalendarJob> logger)
    {
        _logger = logger;
    }

    public ValueTask ExecuteAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _logger.LogInformation("Calendar job ran.");
        return ValueTask.CompletedTask;
    }
}
