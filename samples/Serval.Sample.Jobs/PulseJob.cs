#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.Extensions.Logging;
using Serval.Jobs;

namespace Serval.Sample.Jobs;

[PeriodicJob(typeof(Schedules), nameof(Schedules.PulseInterval), Name = "pulse")]
public sealed class PulseJob : IJob
{
    private readonly ILogger<PulseJob> _logger;

    public PulseJob(ILogger<PulseJob> logger)
    {
        _logger = logger;
    }

    public async ValueTask ExecuteAsync(CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromSeconds(1), ct);
        _logger.LogInformation("Pulse ran.");
    }
}
