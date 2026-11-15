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

namespace Serval.Sample.Middleware;

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
        var port = host.Configuration.GetValue("port", 8103);
        host.Services.AddSingleton(new MaintenanceOptions(host.Configuration.GetValue("maintenance", false)));
        host.Services.AddScoped<RequestStamp>();

        // This middleware measures downstream execution; the lifecycle sample observes the final response outcome.
        host.Services.AddServal().UseMiddleware<PipelineTimingMiddleware>().AddGeneratedControllers().AddHttpListener(options =>
        {
            options.Prefixes.Clear();
            options.Prefixes.Add($"http://localhost:{port}/");
        });

        return host.Build();
    }
}
