using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Serval.Hosting;
using Serval.Transport.HttpListener;
using Serval.Transport.Kestrel;

namespace Serval.Transport.Benchmarks;

internal sealed record ServerMetrics(long AllocatedBytes, double CpuMilliseconds, long WorkingSetBytes, int Gen0, int Gen1, int Gen2);

internal static class BenchmarkServer
{
    internal static readonly byte[] SmallBody = "Hello from Serval!"u8.ToArray();
    internal static readonly byte[] UploadBody = CreateBody(64 * 1024);
    internal static readonly byte[] DownloadBody = CreateBody(256 * 1024);

    public static async Task RunAsync(string transport, int port)
    {
        using var certificate = CreateCertificate();
        var webBuilder = transport.StartsWith("kestrel", StringComparison.Ordinal) ? WebApplication.CreateSlimBuilder() : null;
        IHostApplicationBuilder builder = webBuilder is null ? Host.CreateApplicationBuilder() : webBuilder;
        builder.Logging.ClearProviders();
        var serval = builder.Services.AddServal();
        serval.AddEndpoint("GET", "/small", context =>
        {
            context.Response.Headers["Content-Length"] = SmallBody.Length.ToString();
            return context.Response.WriteAsync(SmallBody, context.RequestAborted);
        });
        serval.AddEndpoint("GET", "/download", context =>
        {
            context.Response.Headers["Content-Length"] = DownloadBody.Length.ToString();
            return context.Response.WriteAsync(DownloadBody, context.RequestAborted);
        });
        serval.AddEndpoint("POST", "/echo", async context =>
        {
            var body = await context.Request.ReadBodyAsync(context.RequestAborted);
            context.Response.Headers["Content-Length"] = body.Length.ToString();
            await context.Response.WriteAsync(body, context.RequestAborted);
        });
        switch (transport)
        {
            case "kestrel1":
            case "kestrel2":
            case "kestrel3":
                if (transport == "kestrel3")
                {
                    webBuilder!.WebHost.UseQuic();
                }

                webBuilder!.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, port, listener =>
                {
                    listener.Protocols = transport switch
                    {
                        "kestrel1" => HttpProtocols.Http1,
                        "kestrel2" => HttpProtocols.Http2,
                        _ => HttpProtocols.Http3
                    };
                    if (transport != "kestrel1")
                    {
                        listener.UseHttps(certificate);
                    }
                }));
                break;
            case "listener":
                serval.AddHttpListener(options =>
                {
                    options.Prefixes.Clear();
                    options.Prefixes.Add($"http://localhost:{port}/");
                });
                break;
            default:
                throw new ArgumentException($"Unknown transport: {transport}");
        }

        using IHost host = webBuilder is null ? ((HostApplicationBuilder)builder).Build() : webBuilder.Build();
        if (host is WebApplication application)
        {
            application.UseServal();
        }

        await host.StartAsync();
        Console.WriteLine("READY");
        while (await Console.In.ReadLineAsync() is { } command && command != "stop")
        {
            if (command == "metrics")
            {
                using var process = Process.GetCurrentProcess();
                Console.WriteLine(JsonSerializer.Serialize(new ServerMetrics(GC.GetTotalAllocatedBytes(true),
                    process.TotalProcessorTime.TotalMilliseconds, process.WorkingSet64,
                    GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2))));
            }
        }

        using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await host.StopAsync(shutdown.Token);
    }

    private static byte[] CreateBody(int length)
    {
        var body = new byte[length];
        for (var index = 0; index < body.Length; index++)
        {
            body[index] = (byte)(index % 251);
        }

        return body;
    }

    private static X509Certificate2 CreateCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.Exportable);
    }
}
