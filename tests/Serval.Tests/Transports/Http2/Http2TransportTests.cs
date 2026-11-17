#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Text;
using Serval.Generated;
using Serval.Hosting;
using Serval.Http.Constants;
using Serval.Tests.Support;
using Serval.Transport.Http2;

namespace Serval.Tests.Transports.Http2;

public sealed class Http2TransportTests
{
    private static readonly SocketsHttpHandler ClientHandler = new()
    {
        UseProxy                     = false,
        InitialHttp2StreamWindowSize = 65535,
        SslOptions =
        {
            RemoteCertificateValidationCallback = (_, _, _, _) => true
        },
    };

    [Fact]
    public async Task RepeatedShutdownIsSafeWhileAcceptIsPending()
    {
        for (var iteration = 0; iteration < 10; iteration++)
        {
            var builder = Host.CreateApplicationBuilder();
            builder.Logging.ClearProviders();
            builder.Services.AddServal().AddHttp2(options =>
            {
                options.EndPoint = new IPEndPoint(IPAddress.Loopback, 0);
                options.UseTls   = false;
            });
            using var host = builder.Build();
            await host.StartAsync();
            await Task.Yield();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await host.StopAsync(timeout.Token);
            await host.StopAsync(timeout.Token);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ServesMultiplexedRequestsLargeBodiesFilesAndEvents(bool tls)
    {
        var directory = Path.Combine(Path.GetTempPath(), "serval-http2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var file = new byte[512 * 1024];
        Random.Shared.NextBytes(file);
        await File.WriteAllBytesAsync(Path.Combine(directory, "download.bin"), file);
        await using var server = await TransportTestHost.StartAsync(HttpVersion.Version20, tls, ClientHandler,
            serval => Serval_Sample_TransportsControllers.Register(serval), options => options.StaticFiles.DirectoryPath = directory);
        var       client = server.Client;
        using var first  = await client.GetAsync("/test/items/12");
        Assert.Equal(HttpVersion.Version20, first.Version);
        Assert.Equal("12:controller,method", await first.Content.ReadAsStringAsync());
        var pending = new Task<string>[30];
        for (var index = 0; index < pending.Length; index++)
        {
            pending[index] = client.GetStringAsync($"/test/items/{index + 1}");
        }

        var results = await Task.WhenAll(pending);
        Assert.Equal(30, results.Length);

        var       text    = new string('x', 200_000);
        using var content = new StringContent("{\"Text\":\"" + text + "\"}", Encoding.UTF8, MimeTypes.Json);
        using var posted  = await client.PostAsync("/test/json", content);
        Assert.Equal(HttpStatusCode.OK, posted.StatusCode);
        Assert.Contains(text, await posted.Content.ReadAsStringAsync());
        Assert.Equal(file, await client.GetByteArrayAsync("/download.bin"));

        using var request = Message(HttpMethod.Get, "/test/task");
        request.Headers.Add("X-Large", new string('z', 30_000));
        using var largeRequestHeaders = await client.SendAsync(request);
        Assert.Equal("task", await largeRequestHeaders.Content.ReadAsStringAsync());
        using var largeResponseHeaders = await client.GetAsync("/test/headers");
        Assert.Equal(20_000, largeResponseHeaders.Headers.GetValues("X-Large").Single().Length);
        Assert.Equal("headers", await largeResponseHeaders.Content.ReadAsStringAsync());

        using var head = await client.SendAsync(Message(HttpMethod.Head, "/test/items/12"));
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.Empty(await head.Content.ReadAsByteArrayAsync());
        using var cookie = await client.GetAsync("/api/cookie");
        Assert.Contains("theme=dark", cookie.Headers.GetValues(HeaderNames.SetCookie).Single());
        using var download = await client.GetAsync("/api/download");
        Assert.Equal("hello.json", download.Content.Headers.ContentDisposition!.FileNameStar);
        Assert.Contains("public directory", await download.Content.ReadAsStringAsync());
        using var error = await client.GetAsync("/test/error");
        Assert.Equal((HttpStatusCode)422, error.StatusCode);
        Assert.Contains("validation_failed", await error.Content.ReadAsStringAsync());

        using var       events      = await client.GetAsync("/api/events", HttpCompletionOption.ResponseHeadersRead);
        await using var eventStream = await events.Content.ReadAsStreamAsync();
        using var       reader      = new StreamReader(eventStream);
        Assert.Equal("event: tick", await reader.ReadLineAsync());
        Assert.Contains("data: Event 2", await reader.ReadToEndAsync());

        using var cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetAsync("/test/slow", cancelled.Token));
        Assert.Equal("task", await client.GetStringAsync("/test/task", CancellationToken.None));
    }

    private static HttpRequestMessage Message(HttpMethod method, string path) => new(method, path)
    {
        Version       = HttpVersion.Version20,
        VersionPolicy = HttpVersionPolicy.RequestVersionExact,
    };
}
