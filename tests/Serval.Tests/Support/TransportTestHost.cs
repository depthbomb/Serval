#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using System.Net;
using System.Net.Quic;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using Serval.Generated;
using Serval.Hosting;
using Serval.Sample.Transports;
using Serval.Transport.HttpListener;
using Serval.Transport.Kestrel;

namespace Serval.Tests.Support;

internal sealed class TransportTestHost : IAsyncDisposable
{
    public HttpClient Client { get; }
    private readonly IHost _host;
    private readonly X509Certificate2? _certificate;

    private TransportTestHost(IHost host, HttpClient client, X509Certificate2? certificate)
    {
        _host = host;
        Client = client;
        _certificate = certificate;
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        try
        {
            using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await _host.StopAsync(shutdown.Token);
        }
        finally
        {
            try
            {
                _host.Dispose();
            }
            finally
            {
                _certificate?.Dispose();
            }
        }
    }

    public Task StopAsync(CancellationToken ct) => _host.StopAsync(ct);

    public static async Task<TransportTestHost> StartAsync(Version version,
                                                           bool tls,
                                                           SocketsHttpHandler clientHandler,
                                                           Action<ServalBuilder>? configure = null,
                                                           Action<ServalOptions>? configureOptions = null,
                                                           TimeSpan? clientTimeout = null,
                                                           CancellationToken ct = default,
                                                           bool kestrel = false,
                                                           Action<KestrelServerOptions>? configureKestrel = null,
                                                           Action<IApplicationBuilder>? configureApplication = null)
    {
        if (version.Major == 3 && !QuicListener.IsSupported)
        {
            throw new PlatformNotSupportedException();
        }

        using var reservation = new Socket(AddressFamily.InterNetwork, version.Major == 3 ? SocketType.Dgram : SocketType.Stream,
            version.Major == 3 ? ProtocolType.Udp : ProtocolType.Tcp);
        reservation.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)reservation.LocalEndPoint!).Port;
        reservation.Close();
        var certificate = tls ? DevelopmentCertificate.Create() : null;
        IHost? host = null;
        HttpClient? client = null;
        try
        {
            var webBuilder = kestrel || version.Major > 1 ? WebApplication.CreateSlimBuilder() : null;
            IHostApplicationBuilder builder = webBuilder is null ? Host.CreateApplicationBuilder() : webBuilder;
            builder.Logging.ClearProviders();
            builder.Services.AddScoped<ScopedProbe>();
            var serval = builder.Services.AddServal(configureOptions).AddGeneratedControllers();
            configure?.Invoke(serval);
            if (webBuilder is not null)
            {
                if (version.Major == 3)
                {
                    webBuilder.WebHost.UseQuic();
                }

                webBuilder.WebHost.ConfigureKestrel(options =>
                {
                    options.Listen(IPAddress.Loopback, port, listener =>
                    {
                        listener.Protocols = version.Major switch
                        {
                            1 => HttpProtocols.Http1,
                            2 => HttpProtocols.Http2,
                            3 => HttpProtocols.Http3,
                            _ => throw new ArgumentOutOfRangeException(nameof(version))
                        };
                        if (tls)
                        {
                            listener.UseHttps(certificate!);
                        }
                    });
                    configureKestrel?.Invoke(options);
                });
                var application = webBuilder.Build();
                configureApplication?.Invoke(application);
                application.UseServal();
                host = application;
            }
            else
            {
                serval.AddHttpListener(options =>
                {
                    options.Prefixes.Clear();
                    options.Prefixes.Add($"http://localhost:{port}/");
                });
                host = ((HostApplicationBuilder)builder).Build();
            }

            await host.StartAsync(ct);
            // The caller's handler retains its connection pool and transport-specific settings.
            client = new HttpClient(clientHandler, false);
            var hostname = version.Major == 1 ? "localhost" : "127.0.0.1";
            client.BaseAddress = new Uri($"{(tls ? "https" : "http")}://{hostname}:{port}");
            client.DefaultRequestVersion = version;
            client.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact;
            client.Timeout = clientTimeout ?? TimeSpan.FromSeconds(15);
            return new TransportTestHost(host, client, certificate);
        }
        catch
        {
            client?.Dispose();
            try
            {
                host?.Dispose();
            }
            finally
            {
                certificate?.Dispose();
            }

            throw;
        }
    }
}
