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
using Serval.Transport.Http2;
using Serval.Transport.Http3;
using Serval.Transport.HttpListener;
using Serval.Transport.Kestrel;

namespace Serval.Sample.Transports;

public static class Program
{
    public static async Task Main(string[] args)
    {
        if (args.Contains("--kestrel", StringComparer.Ordinal))
        {
            await RunKestrelAsync(args);
            return;
        }

        var enableHttp3 = args.Contains("--http3", StringComparer.Ordinal);
        var enableHttp2 = args.Contains("--http2", StringComparer.Ordinal);
        var host = Host.CreateApplicationBuilder([.. args.Where(argument => argument is not ("--http2" or "--http3"))]);
        var port = host.Configuration.GetValue("port", 8110);
        var serval = ConfigureServal(host).AddHttpListener(options =>
        {
            options.Prefixes.Clear();
            options.Prefixes.Add($"http://localhost:{port}/");
        });

        // The certificate is temporary and untrusted; it is never installed in a trust store.
        // Dispose the host before the certificate so active transports can shut down first.
        using var certificate = enableHttp2 || enableHttp3 ? DevelopmentCertificate.Create() : null;
        if (enableHttp2)
        {
            serval.AddHttp2(options =>
            {
                options.Certificate = certificate;
                options.EndPoint = new IPEndPoint(IPAddress.Loopback, host.Configuration.GetValue("http2-port", 8444));
            });
        }

        if (enableHttp3)
        {
            serval.AddHttp3(options =>
            {
                options.Certificate = certificate;
                options.EndPoint = new IPEndPoint(IPAddress.Loopback, host.Configuration.GetValue("http3-port", 8443));
            });
        }

        var application = host.Build();
        await application.RunAsync();
    }

    private static async Task RunKestrelAsync(string[] args)
    {
        var host = WebApplication.CreateSlimBuilder([.. args.Where(argument => argument != "--kestrel")]);
        host.WebHost.UseQuic();
        using var certificate = DevelopmentCertificate.Create();
        host.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(IPAddress.Loopback, host.Configuration.GetValue("port", 8110));
            options.Listen(IPAddress.Loopback, host.Configuration.GetValue("https-port", 8443), listener =>
            {
                listener.Protocols = HttpProtocols.Http1AndHttp2AndHttp3;
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
            options.TrailingSlashes = TrailingSlashBehavior.RedirectToWithoutSlash;
            options.StaticFiles.DirectoryPath = Path.Combine(AppContext.BaseDirectory, "Public");
            options.StaticFiles.Priority = StaticFilePriority.AfterRouting;
        }).AddGeneratedControllers();
}
