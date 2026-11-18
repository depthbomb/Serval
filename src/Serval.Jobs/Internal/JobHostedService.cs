#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.Extensions.Hosting;

namespace Serval.Jobs.Internal;

internal sealed class JobHostedService : IHostedService
{
    private readonly JobRegistry _registry;

    public JobHostedService(JobRegistry registry)
    {
        _registry = registry;
    }

    public Task StartAsync(CancellationToken ct) => _registry.StartAsync(ct);

    public Task StopAsync(CancellationToken ct) => _registry.StopAsync(ct);
}
