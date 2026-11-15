#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using System.Security.Cryptography;
using Serval.Generated;
using Serval.Hosting;
using Serval.SignedUrls;
using Serval.Transport.HttpListener;

namespace Serval.Sample.SignedUrls;

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
        var port = host.Configuration.GetValue("port", 8115);

        // A fresh key keeps the demo runnable without setup. Configure a persistent secret for links that survive restarts.
        var configuredKey = host.Configuration["SignedUrls:Key"];
        var signingKey    = configuredKey is null ? RandomNumberGenerator.GetBytes(32) : Convert.FromBase64String(configuredKey);
        host.Services.AddServal().AddGeneratedControllers().AddSignedUrls(options => options.SigningKey = signingKey).AddHttpListener(options =>
        {
            options.Prefixes.Clear();
            options.Prefixes.Add($"http://localhost:{port}/");
        });

        return host.Build();
    }
}
