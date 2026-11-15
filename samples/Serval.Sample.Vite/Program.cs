#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Serval.Generated;
using Serval.Hosting;
using Serval.Http.StaticFiles;
using Serval.Sample.Vite.Generated;
using Serval.Transport.HttpListener;

namespace Serval.Sample.Vite;

public static class Program
{
    public static async Task Main(string[] args)
    {
        using var host = Build(args);
        await host.RunAsync();
    }

    public static IHost Build(string[] args)
    {
        var host = Host.CreateApplicationBuilder(args);
        var port = host.Configuration.GetValue("port", 8114);
        host.Services.AddServal(options =>
        {
            if (!Assets.IsDevelopment)
            {
                options.StaticFiles.DirectoryPath = Path.Combine(AppContext.BaseDirectory, "Public");
                options.StaticFiles.Priority      = StaticFilePriority.AfterRouting;
                // Vite gives changed assets new filenames, so these URLs can be cached for a year.
                options.StaticFiles.CacheControl  = "public, max-age=31536000, immutable";
            }
        }).AddGeneratedControllers().AddHttpListener(options =>
        {
            options.Prefixes.Clear();
            options.Prefixes.Add($"http://localhost:{port}/");
        });

        return host.Build();
    }
}
