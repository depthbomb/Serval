#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serval.Generated;
using Serval.Hosting;
using Serval.Transport.HttpListener;

namespace Serval.Sample.BackgroundServices;

public static class Program
{
    public static async Task Main(string[] args)
    {
        var host = Build(args);
        await host.RunAsync();
    }

    public static IHost Build(string[] args)
    {
        var host = Host.CreateApplicationBuilder(args);
        var port = host.Configuration.GetValue("port", 8112);
        var connectionString = host.Configuration.GetConnectionString("Catalog") ?? new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(AppContext.BaseDirectory, "catalog.db"),
        }.ToString();

        host.Services.AddOptions<CatalogOptions>().Bind(host.Configuration.GetSection("Catalog")).Validate(options => !string.IsNullOrWhiteSpace(options.SourcePath), "Catalog:SourcePath is required.").Validate(options => options.RefreshInterval >= TimeSpan.FromMilliseconds(50) && options.RefreshInterval <= TimeSpan.FromHours(1), "Catalog:RefreshInterval must be between 50 ms and one hour.").ValidateOnStart();

        // SQLite keeps the sample self-contained. Its provider executes database
        // I/O synchronously internally; all application database calls use async APIs.
        host.Services.AddDbContext<CatalogDbContext>(options => options.UseSqlite(connectionString));
        host.Services.AddScoped<CatalogImporter>();
        host.Services.AddSingleton<CatalogImportState>();
        host.Services.AddSingleton(TimeProvider.System);
        host.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(10));

        // Startup services run in registration order by default. Load the first
        // catalog before starting the worker and accepting HTTP requests.
        host.Services.AddHostedService<CatalogInitializer>();
        host.Services.AddHostedService<CatalogRefreshService>();
        host.Services.AddServal().AddGeneratedControllers().AddHttpListener(options =>
        {
            options.Prefixes.Clear();
            options.Prefixes.Add($"http://localhost:{port}/");
        });

        return host.Build();
    }
}
