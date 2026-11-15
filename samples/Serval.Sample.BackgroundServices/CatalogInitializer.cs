#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Serval.Sample.BackgroundServices;

public sealed class CatalogInitializer : IHostedService
{
    private readonly IServiceScopeFactory _scopes;

    public CatalogInitializer(IServiceScopeFactory scopes)
    {
        _scopes = scopes;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var scope    = _scopes.CreateAsyncScope();
        var             database = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        // This database is a rebuildable local cache, so EnsureCreated is enough.
        await database.Database.EnsureCreatedAsync(cancellationToken);
        await scope.ServiceProvider.GetRequiredService<CatalogImporter>().RefreshAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
