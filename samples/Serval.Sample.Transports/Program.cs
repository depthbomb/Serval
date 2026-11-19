#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using System.Net;
using Serval.Generated;
using Serval.Hosting;
using Serval.Http.StaticFiles;
using Serval.Transport.HttpListener;
using Serval.Transport.Kestrel;

namespace Serval.Sample.Transports;

public static class Program
{
    public static async Task Main(string[] args)
    {
        if (!args.Contains("--http-listener", StringComparer.Ordinal))
        {
            await RunKestrelAsync(args);
            return;
        }

        if (args.Contains("--http3", StringComparer.Ordinal))
        {
            throw new ArgumentException("--http3 requires Kestrel; omit --http-listener.");
        }

        var host = Host.CreateApplicationBuilder([.. args.Where(argument => argument != "--http-listener")]);
        var port = host.Configuration.GetValue("port", 8110);
        ConfigureServal(host).AddHttpListener(options =>
        {
            options.Prefixes.Clear();
            options.Prefixes.Add($"http://localhost:{port}/");
        });

        var application = host.Build();
        await application.RunAsync();
    }

    private static async Task RunKestrelAsync(string[] args)
    {
        var enableHttp3 = args.Contains("--http3", StringComparer.Ordinal);
        var host = WebApplication.CreateSlimBuilder([.. args.Where(argument => argument != "--http3")]);
        if (enableHttp3)
        {
            host.WebHost.UseQuic();
        }

        // The certificate is temporary and untrusted; it is never installed in a trust store.
        using var certificate = DevelopmentCertificate.Create();
        host.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(IPAddress.Loopback, host.Configuration.GetValue("port", 8110),
                listener => listener.Protocols = HttpProtocols.Http1);
            options.Listen(IPAddress.Loopback, host.Configuration.GetValue("https-port", 8443), listener =>
            {
                listener.Protocols = enableHttp3 ? HttpProtocols.Http1AndHttp2AndHttp3 : HttpProtocols.Http1AndHttp2;
                listener.UseHttps(certificate);
            });
        });
        ConfigureServal(host);
        await using var application = host.Build();
        application.UseServal();
        await application.RunAsync();
    }

    private static ServalBuilder ConfigureServal(IHostApplicationBuilder host)
        => host.Services.AddServal(options =>
        {
            options.TrailingSlashes           = TrailingSlashBehavior.RedirectToWithoutSlash;
            options.StaticFiles.DirectoryPath = Path.Combine(AppContext.BaseDirectory, "Public");
            options.StaticFiles.Priority      = StaticFilePriority.AfterRouting;
        }).AddGeneratedControllers();
}
