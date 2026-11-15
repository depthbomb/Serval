#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serval.Generated;
using Serval.Hosting;
using Serval.Transport.HttpListener;

namespace Serval.Sample.Lifecycles;

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
        var port = host.Configuration.GetValue("port", 8117);
        host.Services.AddScoped<RequestDetails>();

        var serval = host.Services.AddServal(options =>
        {
            options.TrailingSlashes           = TrailingSlashBehavior.RedirectToWithoutSlash;
            options.RequestTimeout            = TimeSpan.FromMilliseconds(host.Configuration.GetValue("timeout-ms", 2000));
            options.StaticFiles.DirectoryPath = Path.Combine(AppContext.BaseDirectory, "Public");
        });
        serval.AddRequestLifecycleHandler<RequestTimingHandler>(priority: -100);
        serval.AddRequestLifecycleHandler<ResponseHeadersHandler>();
        serval.AddRequestLifecycleHandler<RequestAuditHandler>(priority: 100);
        serval.AddGeneratedControllers().AddHttpListener(options =>
        {
            options.Prefixes.Clear();
            options.Prefixes.Add($"http://localhost:{port}/");
        });

        return host.Build();
    }
}
