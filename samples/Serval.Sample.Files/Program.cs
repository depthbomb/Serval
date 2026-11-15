#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Serval.Generated;
using Serval.Hosting;
using Serval.Http.StaticFiles;
using Serval.Transport.HttpListener;

namespace Serval.Sample.Files;

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
        var port = host.Configuration.GetValue("port", 8104);
        host.Services.AddServal(options =>
        {
            options.MaxRequestBodyBytes       = 64 * 1024;
            options.StaticFiles.DirectoryPath = Path.Combine(AppContext.BaseDirectory, "Public");
            options.StaticFiles.Priority      = StaticFilePriority.AfterRouting;
            options.StaticFiles.CacheControl  = "public, max-age=60";
        }).AddGeneratedControllers().AddHttpListener(options =>
        {
            options.Prefixes.Clear();
            options.Prefixes.Add($"http://localhost:{port}/");
        });

        return host.Build();
    }
}
