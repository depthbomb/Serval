#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Quic;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Serval.Generated;
using Serval.Hosting;
using Serval.Http.Constants;
using Serval.Sample.Transports;
using Serval.Tests.Support;
using Serval.Transport.Http2;
using Serval.Transport.Http3;
using Serval.Transport.HttpListener;

namespace Serval.Tests.Transports;

public sealed class QueryTransportTests
{
    private static readonly SocketsHttpHandler ClientHandler = new()
    {
        UseProxy   = false,
        SslOptions = { RemoteCertificateValidationCallback = (_, _, _, _) => true },
    };

    [Fact]
    public Task HttpListenerServesQueryBodiesAndErrors() => ExerciseAsync(HttpVersion.Version11, false);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Http2ServesQueryBodiesAndErrors(bool tls) => ExerciseAsync(HttpVersion.Version20, tls);

    [QuicFact]
    public Task Http3ServesQueryBodiesAndErrors() => ExerciseAsync(HttpVersion.Version30, true);

    private static async Task ExerciseAsync(Version version, bool tls)
    {
        if (version.Major == 3 && !QuicListener.IsSupported)
        {
            throw new PlatformNotSupportedException();
        }

        using var timeout     = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var certificate = tls ? DevelopmentCertificate.Create() : null;
        using var reservation = new Socket(AddressFamily.InterNetwork, version.Major == 3 ? SocketType.Dgram : SocketType.Stream,
            version.Major == 3 ? ProtocolType.Udp : ProtocolType.Tcp);
        reservation.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)reservation.LocalEndPoint!).Port;
        reservation.Close();

        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddScoped<ScopedProbe>();
        var serval = builder.Services.AddServal().AddGeneratedControllers();
        switch (version.Major)
        {
            case 1:
                serval.AddHttpListener(options =>
                {
                    options.Prefixes.Clear();
                    options.Prefixes.Add($"http://localhost:{port}/");
                });
                break;
            case 2:
                serval.AddHttp2(options =>
                {
                    options.EndPoint    = new IPEndPoint(IPAddress.Loopback, port);
                    options.UseTls      = tls;
                    // Configuration runs immediately; the certificate outlives the host.
                    // ReSharper disable once AccessToDisposedClosure
                    options.Certificate = certificate;
                });
                break;
            case 3:
                serval.AddHttp3(options =>
                {
                    options.EndPoint    = new IPEndPoint(IPAddress.Loopback, port);
                    // Configuration runs immediately; the certificate outlives the host.
                    // ReSharper disable once AccessToDisposedClosure
                    options.Certificate = certificate;
                });
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(version));
        }

        using var host = builder.Build();
        await host.StartAsync(timeout.Token);
        try
        {
            // The handler owns the shared connection pool; this test owns its client wrapper.
            // ReSharper disable once ShortLivedHttpClient
            using var client = new HttpClient(ClientHandler, false);
            var hostname = version.Major == 1 ? "localhost" : "127.0.0.1";
            client.BaseAddress           = new Uri($"{(tls ? "https" : "http")}://{hostname}:{port}");
            client.DefaultRequestVersion = version;
            client.DefaultVersionPolicy  = HttpVersionPolicy.RequestVersionExact;
            client.Timeout               = TimeSpan.FromSeconds(15);

            foreach (var text in new[] { "find café", new string('x', 70_000) + " café" })
            {
                var body = JsonSerializer.Serialize(new Message(text), TestJsonContext.Default.Message);
                using var response = await SendQueryAsync(client, "/query/search", body, MimeTypes.Json, timeout.Token);
                Assert.Equal(version, response.Version);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.Equal(text, await response.Content.ReadAsStringAsync(timeout.Token));
                Assert.Equal("\"application/json\"", Assert.Single(response.Headers.GetValues(HeaderNames.AcceptQuery)));
            }

            foreach (var (body, contentType, status) in new[]
            {
                ("{}", null, HttpStatusCode.BadRequest),
                ("{}", "invalid", HttpStatusCode.BadRequest),
                ("{", MimeTypes.Json, HttpStatusCode.BadRequest),
                ("{}", MimeTypes.PlainText, HttpStatusCode.UnsupportedMediaType),
            })
            {
                using var response = await SendQueryAsync(client, "/query/search", body, contentType, timeout.Token);
                Assert.Equal(version, response.Version);
                Assert.Equal(status, response.StatusCode);
            }

            using var raw = await SendQueryAsync(client, "/query/raw", "query", null, timeout.Token);
            Assert.Equal(HttpStatusCode.BadRequest, raw.StatusCode);
            using var form = await SendQueryAsync(client, "/query/form", "term=caf%C3%A9+books", MimeTypes.FormUrlEncoded, timeout.Token);
            Assert.Equal(HttpStatusCode.OK, form.StatusCode);
            Assert.Equal("café books", await form.Content.ReadAsStringAsync(timeout.Token));

            foreach (var method in new[] { HttpMethod.Options, HttpMethod.Get, HttpMethod.Head })
            {
                using var request = new HttpRequestMessage(method, "/query/search");
                request.Version       = version;
                request.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
                using var response = await client.SendAsync(request, timeout.Token);
                Assert.Equal(version, response.Version);
                Assert.Equal(method == HttpMethod.Options ? HttpStatusCode.NoContent : HttpStatusCode.MethodNotAllowed, response.StatusCode);
                Assert.Equal([HttpMethods.Options, HttpMethods.Query], response.Content.Headers.Allow);
            }

            // An invalid query must not prevent a later request on the same client from succeeding.
            using var final = await SendQueryAsync(client, "/query/custom", "{\"Text\":\"still healthy\"}", MimeTypes.Json, timeout.Token);
            Assert.Equal(HttpStatusCode.OK, final.StatusCode);
            Assert.Equal("still healthy", await final.Content.ReadAsStringAsync(timeout.Token));
        }
        finally
        {
            using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await host.StopAsync(shutdown.Token);
        }
    }

    private static async Task<HttpResponseMessage> SendQueryAsync(HttpClient client, string path, string body, string? contentType, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(new HttpMethod(HttpMethods.Query), path);
        request.Version       = client.DefaultRequestVersion;
        request.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
        request.Content       = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
        if (contentType is not null)
        {
            Assert.True(request.Content.Headers.TryAddWithoutValidation(HeaderNames.ContentType, contentType));
        }

        return await client.SendAsync(request, ct);
    }

    private sealed class QuicFactAttribute : FactAttribute
    {
        public QuicFactAttribute()
        {
            if (!QuicListener.IsSupported)
            {
                Skip = "System.Net.Quic is unavailable on this host.";
            }
        }
    }
}
