#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Caprine.CronTimer;

namespace Serval.Jobs.Internal;

internal sealed class JobTimer : IDisposable
{
    private readonly PeriodicTimer? _periodic;
    private readonly CronTimer?     _cron;

    internal JobTimer(JobSchedule schedule, TimeProvider timeProvider)
    {
        if (schedule.Period is {} period)
        {
            _periodic = new PeriodicTimer(period, timeProvider);
        }
        else
        {
            _cron = new CronTimer(schedule.Expression!, timeProvider, schedule.TimeZone);
        }
    }

    public void Dispose()
    {
        _periodic?.Dispose();
        _cron?.Dispose();
    }

    internal ValueTask<bool> WaitForNextTickAsync(CancellationToken ct)
        => _periodic?.WaitForNextTickAsync(ct) ?? _cron!.WaitForNextTickAsync(ct);
}
