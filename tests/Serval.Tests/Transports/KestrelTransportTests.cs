#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using System.Net;
using System.Net.Quic;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Serval.Generated;
using Serval.Tests.Support;

namespace Serval.Tests.Transports;

public sealed class KestrelTransportTests
{
    [Fact]
    public async Task Http10WithoutHostUsesTheLocalEndpoint()
    {
        using var handler = new SocketsHttpHandler();
        handler.UseProxy = false;
        await using var host = await TransportTestHost.StartAsync(HttpVersion.Version10, false, handler, serval =>
            serval.AddEndpoint("GET", "/legacy", context =>
            {
                Assert.Equal(HttpVersion.Version10, context.Request.ProtocolVersion);
                Assert.True(IPAddress.IsLoopback(IPAddress.Parse(context.Request.Url.Host)));
                Assert.Equal(context.Request.Connection.LocalPort, context.Request.Url.Port);
                return context.Response.WriteTextAsync("legacy");
            }), kestrel: true);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var client  = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, host.Client.BaseAddress!.Port, timeout.Token);
        await using var stream = client.GetStream();
        await stream.WriteAsync("GET /legacy HTTP/1.0\r\n\r\n"u8.ToArray(), timeout.Token);
        using var reader   = new StreamReader(stream, Encoding.ASCII);
        var       response = await reader.ReadToEndAsync(timeout.Token);
        Assert.Contains(" 200 ", response.Split("\r\n")[0]);
        Assert.EndsWith("legacy", response);
    }

    public static TheoryData<int, bool> Protocols()
    {
        var protocols = new TheoryData<int, bool>
        {
            { 1, false },
            { 2, false },
            { 2, true },
        };
        if (QuicListener.IsSupported)
        {
            protocols.Add(3, true);
        }

        return protocols;
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task ServesControllersBodiesHeadersHeadAndErrors(int major, bool tls)
    {
        using var handler = CreateHandler();
        var       version = VersionFor(major);
        await using var host = await TransportTestHost.StartAsync(version, tls, handler, serval =>
        {
            Serval_Sample_TransportsControllers.Register(serval);
            serval.AddEndpoint("GET", "/headers", async context =>
            {
                context.Response.Headers.Append("Set-Cookie", "first=one; Path=/");
                context.Response.Headers.Append("Set-Cookie", "second=two; Path=/");
                context.Response.OnStarting(() =>
                {
                    context.Response.Headers["X-Starting"] = "yes";
                    return ValueTask.CompletedTask;
                });
                await context.Response.WriteTextAsync(context.Request.ProtocolVersion.ToString());
            });
            serval.AddEndpoint("GET", "/empty", context =>
            {
                context.Response.StatusCode = 204;
                return context.Response.WriteTextAsync("suppressed");
            });
        }, kestrel: true);
        using var response = await host.Client.GetAsync("/test/items/12");
        Assert.Equal(version, response.Version);
        Assert.Equal("12:controller,method", await response.Content.ReadAsStringAsync());
        var responses = await Task.WhenAll(Enumerable.Range(1, 30).Select(index => host.Client.GetStringAsync($"/test/items/{index}")));
        Assert.Equal(30, responses.Distinct().Count());
        var       json    = "{\"Text\":\"" + new string('x', 200_000) + "\"}";
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var posted  = await host.Client.PostAsync("/test/json", content);
        Assert.Equal(HttpStatusCode.OK, posted.StatusCode);
        Assert.Equal(json, await posted.Content.ReadAsStringAsync());
        using var headRequest = new HttpRequestMessage(HttpMethod.Head, "/test/items/12");
        headRequest.Version       = version;
        headRequest.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
        using var head = await host.Client.SendAsync(headRequest);
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.Empty(await head.Content.ReadAsByteArrayAsync());
        using var headers = await host.Client.GetAsync("/headers");
        Assert.Equal(2, headers.Headers.GetValues("Set-Cookie").Count());
        Assert.Equal("yes", Assert.Single(headers.Headers.GetValues("X-Starting")));
        Assert.Equal(version.ToString(), await headers.Content.ReadAsStringAsync());
        using var empty = await host.Client.GetAsync("/empty");
        Assert.Equal(HttpStatusCode.NoContent, empty.StatusCode);
        Assert.Empty(await empty.Content.ReadAsByteArrayAsync());
        using var error = await host.Client.GetAsync("/test/error");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, error.StatusCode);
        Assert.Contains("validation_failed", await error.Content.ReadAsStringAsync());
        using var missing = await host.Client.GetAsync("/missing");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task FlushesStreamingDataBeforeCompletionAndDisposesRequestScope(int major, bool tls)
    {
        using var handler   = CreateHandler();
        var       release   = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var       completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var       disposed  = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = await TransportTestHost.StartAsync(VersionFor(major), tls, handler, serval =>
        {
            serval.Services.AddScoped(_ => new DisposalProbe(disposed));
            serval.AddEndpoint("GET", "/stream", async context =>
            {
                _ = context.Services.GetRequiredService<DisposalProbe>();
                context.Response.OnCompleted(() =>
                {
                    Assert.False(disposed.Task.IsCompleted);
                    completed.TrySetResult();
                    return ValueTask.CompletedTask;
                });
                await context.Response.WriteTextAsync("first");
                await context.Response.FlushAsync();
                await release.Task.WaitAsync(context.RequestAborted);
                await context.Response.WriteTextAsync("last");
            });
        }, kestrel: true);
        try
        {
            using var       response = await host.Client.GetAsync("/stream", HttpCompletionOption.ResponseHeadersRead);
            await using var stream   = await response.Content.ReadAsStreamAsync();
            var             first    = new byte[5];
            await stream.ReadExactlyAsync(first).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("first", Encoding.UTF8.GetString(first));
            Assert.False(completed.Task.IsCompleted);
            release.TrySetResult();
            using var reader = new StreamReader(stream);
            Assert.Equal("last", await reader.ReadToEndAsync());
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task EnforcesServalAndKestrelBodyLimitsAndHeaderLimits(int major, bool tls)
    {
        using var handler = CreateHandler();
        await using var host = await TransportTestHost.StartAsync(VersionFor(major), tls, handler, serval =>
        {
            serval.AddEndpoint("POST", "/body", async context =>
            {
                await context.Request.ReadBodyAsync();
                await context.Response.WriteTextAsync("accepted");
            });
        }, options =>
        {
            options.MaxRequestBodyBytes   = 128;
            options.MaxRequestHeaderBytes = 1024;
        }, kestrel: true, configureKestrel: options => options.Limits.MaxRequestBodySize = 64);
        foreach (var length in new[] { 100, 200 })
        {
            using var content  = new ByteArrayContent(new byte[length]);
            using var response = await host.Client.PostAsync("/body", content);
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, "/body");
        request.Version       = VersionFor(major);
        request.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
        request.Headers.Add("X-Large", new string('x', 2000));
        using var oversized = await host.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.RequestHeaderFieldsTooLarge, oversized.StatusCode);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, true)]
    public async Task SupportsWebSocketsAndUpgradeHeaders(int major, bool tls)
    {
        using var handler = CreateHandler();
        await using var host = await TransportTestHost.StartAsync(VersionFor(major), tls, handler, serval =>
        {
            serval.AddEndpoint(major == 1 ? "GET" : "CONNECT", "/socket", async context =>
            {
                context.Response.Headers["X-Upgrade"] = "serval";
                using var socket  = await context.Response.AcceptWebSocketAsync("echo", context.RequestAborted);
                var       buffer  = new byte[128];
                var       message = await socket.ReceiveAsync(buffer.AsMemory(), context.RequestAborted);
                await socket.SendAsync(buffer.AsMemory(0, message.Count), message.MessageType, message.EndOfMessage, context.RequestAborted);
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", context.RequestAborted);
            });
        }, kestrel: true);
        using var socket = new ClientWebSocket();
        socket.Options.AddSubProtocol("echo");
        socket.Options.HttpVersion                = VersionFor(major);
        socket.Options.HttpVersionPolicy          = HttpVersionPolicy.RequestVersionExact;
        socket.Options.CollectHttpResponseDetails = true;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var invoker = new HttpMessageInvoker(handler, false);
        await socket.ConnectAsync(new Uri($"{(tls ? "wss" : "ws")}://127.0.0.1:{host.Client.BaseAddress!.Port}/socket"), invoker, timeout.Token);
        Assert.Equal("echo", socket.SubProtocol);
        Assert.Equal("serval", Assert.Single(socket.HttpResponseHeaders!["X-Upgrade"]));
        await socket.SendAsync("hello"u8.ToArray().AsMemory(), WebSocketMessageType.Text, true, timeout.Token);
        var buffer = new byte[128];
        var result = await socket.ReceiveAsync(buffer.AsMemory(), timeout.Token);
        Assert.Equal("hello", Encoding.UTF8.GetString(buffer, 0, result.Count));
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", timeout.Token);
    }

    [Fact]
    public async Task EnforcesConcurrencyAndCancelsActiveRequestsOnShutdown()
    {
        using var handler   = CreateHandler();
        var       entered   = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var       completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var       disposed  = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = await TransportTestHost.StartAsync(HttpVersion.Version20, false, handler, serval =>
        {
            serval.Services.AddScoped(_ => new DisposalProbe(disposed));
            serval.AddEndpoint("GET", "/wait", async context =>
            {
                _ = context.Services.GetRequiredService<DisposalProbe>();
                context.Response.OnCompleted(() =>
                {
                    completed.TrySetResult();
                    return ValueTask.CompletedTask;
                });
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted);
            });
        }, options => options.MaxConcurrentRequests = 1, kestrel: true);
        var pending = host.Client.GetAsync("/wait");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var rejected = await host.Client.GetAsync("/wait");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, rejected.StatusCode);
        using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await host.StopAsync(shutdown.Token);
        await host.StopAsync(shutdown.Token);
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        await disposed.Task.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            using var response = await pending;
        });
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task ConnectionMetadataRetainsSocketEndpointsAfterUpstreamMiddleware(int major, bool tls)
    {
        using var handler = CreateHandler();
        var       remote  = new TaskCompletionSource<IPEndPoint>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = await TransportTestHost.StartAsync(VersionFor(major), tls, handler, serval =>
            serval.AddEndpoint("GET", "/connection", async context =>
            {
                var endpoint = await remote.Task;
                Assert.Equal(endpoint.Address, context.Request.Connection.RemoteAddress);
                Assert.Equal(endpoint.Port, context.Request.Connection.RemotePort);
                Assert.True(IPAddress.IsLoopback(context.Request.Connection.LocalAddress!));
                Assert.True(context.Request.Connection.LocalPort > 0);
                await context.Response.WriteTextAsync("preserved");
            }), kestrel: true, configureApplication: application => application.Use((context, next) =>
        {
            remote.TrySetResult(new IPEndPoint(context.Connection.RemoteIpAddress!, context.Connection.RemotePort));
            // ASP.NET middleware can replace request-level connection information.
            context.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.20");
            context.Connection.RemotePort      = 1234;
            context.Connection.LocalIpAddress  = IPAddress.Parse("192.0.2.30");
            context.Connection.LocalPort       = 0;
            return next(context);
        }));
        Assert.Equal("preserved", await host.Client.GetStringAsync("/connection"));
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task PreservesRequestDeadlinesAndConnectionMetadata(int major, bool tls)
    {
        using var handler = CreateHandler();
        await using var host = await TransportTestHost.StartAsync(VersionFor(major), tls, handler, serval =>
        {
            serval.AddEndpoint("GET", "/inspect", context =>
            {
                Assert.True(IPAddress.IsLoopback(context.Request.Connection.RemoteAddress!));
                Assert.True(context.Request.Connection.RemotePort > 0);
                Assert.True(context.Request.Connection.LocalPort  > 0);
                Assert.Equal(tls ? "https" : "http", context.Request.Scheme);
                Assert.Equal(["a/b", "a b"], context.Request.Query["value"]);
                return context.Response.WriteTextAsync(context.Request.Url.Query);
            });
            serval.AddEndpoint("GET", "/deadline", async context =>
                await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted));
        }, options => options.RequestTimeout = TimeSpan.FromMilliseconds(100), kestrel: true);
        Assert.Equal("?value=a%2Fb&value=a+b", await host.Client.GetStringAsync("/inspect?value=a%2Fb&value=a+b"));
        using var timeout = await host.Client.GetAsync("/deadline");
        Assert.Equal(HttpStatusCode.RequestTimeout, timeout.StatusCode);
    }

    [Fact]
    public async Task ClientDisconnectCancelsStreamingRequestAndDisposesScope()
    {
        using var handler   = CreateHandler();
        var       cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var       disposed  = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = await TransportTestHost.StartAsync(HttpVersion.Version20, false, handler, serval =>
        {
            serval.Services.AddScoped(_ => new DisposalProbe(disposed));
            serval.AddEndpoint("GET", "/stream", async context =>
            {
                _ = context.Services.GetRequiredService<DisposalProbe>();
                await using var registration = context.RequestAborted.Register(() => cancelled.TrySetResult());
                await context.Response.WriteTextAsync("started");
                await context.Response.FlushAsync();
                await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted);
            });
        }, kestrel: true);
        using (var response = await host.Client.GetAsync("/stream", HttpCompletionOption.ResponseHeadersRead))
        {
            await using var stream = await response.Content.ReadAsStreamAsync();
            await stream.ReadExactlyAsync(new byte[7]);
        }

        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static Version VersionFor(int major) => major switch
    {
        1 => HttpVersion.Version11,
        2 => HttpVersion.Version20,
        _ => HttpVersion.Version30,
    };

    private static SocketsHttpHandler CreateHandler() => new()
    {
        UseProxy = false,
        SslOptions =
        {
            RemoteCertificateValidationCallback = (_, _, _, _) => true,
        },
    };

    private sealed class DisposalProbe : IAsyncDisposable
    {
        private readonly TaskCompletionSource _disposed;

        public DisposalProbe(TaskCompletionSource disposed)
        {
            _disposed = disposed;
        }

        public ValueTask DisposeAsync()
        {
            _disposed.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }
}
