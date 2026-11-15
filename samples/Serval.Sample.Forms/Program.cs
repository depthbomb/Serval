#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Serval.Generated;
using Serval.Hosting;
using Serval.Transport.HttpListener;

namespace Serval.Sample.Forms;

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
        var port = host.Configuration.GetValue("port", 8116);

        host.Services.AddServal(options =>
        {
            options.MaxRequestBodyBytes = 8 * 1024 * 1024;
            options.Forms.MaxFileBytes  = 4 * 1024 * 1024;
            options.Forms.MaxFiles      = 4;
            options.Forms.MaxFields     = 32;
        }).AddGeneratedControllers().AddHttpListener(options =>
        {
            options.Prefixes.Clear();
            options.Prefixes.Add($"http://localhost:{port}/");
        });

        return host.Build();
    }
}
