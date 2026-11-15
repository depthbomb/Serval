#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Data.Common;
using System.Text.Json;

namespace Serval.Sample.BackgroundServices;

public sealed class CatalogRefreshService : BackgroundService
{
    private readonly ILogger<CatalogRefreshService> _logger;
    private readonly IOptions<CatalogOptions>       _options;
    private readonly IServiceScopeFactory           _scopes;
    private readonly CatalogImportState             _state;

    public CatalogRefreshService(IServiceScopeFactory scopes, IOptions<CatalogOptions> options, CatalogImportState state, ILogger<CatalogRefreshService> logger)
    {
        _scopes  = scopes;
        _options = options;
        _state   = state;
        _logger  = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_options.Value.RefreshInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    // A hosted service is a singleton. Each iteration gets a fresh
                    // importer and DbContext, separate from every HTTP request.
                    await using var scope = _scopes.CreateAsyncScope();
                    await scope.ServiceProvider.GetRequiredService<CatalogImporter>().RefreshAsync(stoppingToken);
                }
                catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException or DbException or DbUpdateException)
                {
                    _state.Failed();
                    _logger.LogWarning(exception, "Catalog refresh failed. The next interval will retry.");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutdown cancels both the timer and any in-progress import.
        }
    }
}
