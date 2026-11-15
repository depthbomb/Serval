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

namespace Serval.Sample.EntityFramework;

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
        var port = host.Configuration.GetValue("port", 8111);
        var connectionString = host.Configuration.GetConnectionString("Notes") ?? new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(AppContext.BaseDirectory, "notes.db"),
        }.ToString();

        // AddDbContext is scoped. Serval supplies and asynchronously disposes
        // the request scope, so controllers can inject NotesDbContext directly.
        host.Services.AddDbContext<NotesDbContext>(options => options.UseSqlite(connectionString));
        host.Services.AddHostedService<NotesInitializer>();
        host.Services.AddServal(options => options.MaxRequestBodyBytes = 16 * 1024).AddGeneratedControllers().AddHttpListener(options =>
        {
            options.Prefixes.Clear();
            options.Prefixes.Add($"http://localhost:{port}/");
        });

        return host.Build();
    }
}
