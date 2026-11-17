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
using System.Net.WebSockets;
using System.Text;
using Serval.Generated;
using Serval.Hosting;
using Serval.Http;
using Serval.Http.Constants;
using Serval.Http.Results;
using Serval.Tests.Support;
using Serval.Transport.HttpListener;

namespace Serval.Tests.Transports;

public sealed class TransportTests
{
    private static readonly SocketsHttpHandler ClientHandler = new()
    {
        UseProxy = false,
        SslOptions =
        {
            RemoteCertificateValidationCallback = (_, _, _, _) => true
        },
    };

    [Fact]
    public async Task HttpListenerServesJsonHeadCookiesSseAndWebSockets()
    {
        await using var server = await TransportTestHost.StartAsync(HttpVersion.Version11, false, ClientHandler, serval =>
        {
            Serval_Sample_TransportsControllers.Register(serval);
            serval.AddController<object>(HttpMethods.Get, "/transport/cookies", null, (_, context) =>
            {
                var options = new CookieOptions
                {
                    Secure  = false,
                    Expires = DateTimeOffset.UtcNow.AddDays(1)
                };
                context.Response.Cookies.Set("first", "one", options);
                context.Response.Cookies.Set("second", "two", options);
                return ValueTask.FromResult<IActionResult>(new TextResult("cookies"));
            });
            serval.AddController<object>(HttpMethods.Get, "/transport/early-hints", null, async (_, context) =>
            {
                var hints = new Headers
                {
                    [HeaderNames.Link] = "</site.css>; rel=preload; as=style"
                };
                Assert.False(context.Response.SupportsEarlyHints);
                Assert.False(await context.Response.SendEarlyHintsAsync(hints, context.RequestAborted));
                Assert.False(context.Response.HasStarted);
                context.Response.Headers["X-Final"] = "yes";
                return new TextResult("final");
            });
        }, clientTimeout: TimeSpan.FromSeconds(10));
        var client = server.Client;
        Assert.Equal("12:controller,method", await client.GetStringAsync("/test/items/12"));
        foreach (var json in new[] { "{}", "{\"name\":null}" })
        {
            using var content       = new StringContent(json, Encoding.UTF8, MimeTypes.Json);
            using var invalidWidget = await client.PostAsync("/api/widgets", content);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, invalidWidget.StatusCode);
        }

        using var head = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/test/items/12"));
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.Empty(await head.Content.ReadAsByteArrayAsync());
        using var hints = await client.GetAsync("/transport/early-hints");
        Assert.Equal(HttpStatusCode.OK, hints.StatusCode);
        Assert.Equal("yes", Assert.Single(hints.Headers.GetValues("X-Final")));
        Assert.False(hints.Headers.Contains(HeaderNames.Link));
        Assert.Equal("final", await hints.Content.ReadAsStringAsync());
        using var cookie = await client.GetAsync("/api/cookie");
        Assert.Contains("theme=dark", cookie.Headers.GetValues(HeaderNames.SetCookie).Single());
        using var multipleCookies = await client.GetAsync("/transport/cookies");
        var       cookieHeaders   = multipleCookies.Headers.GetValues(HeaderNames.SetCookie).ToArray();
        Assert.Equal(2, cookieHeaders.Length);
        Assert.Contains(cookieHeaders, value => value.StartsWith("first=one;", StringComparison.Ordinal));
        Assert.Contains(cookieHeaders, value => value.StartsWith("second=two;", StringComparison.Ordinal));
        using var events = await client.GetAsync("/api/events", HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(MimeTypes.EventStream, events.Content.Headers.ContentType!.MediaType);
        await using var body   = await events.Content.ReadAsStreamAsync();
        using var       reader = new StreamReader(body);
        Assert.Equal("event: tick", await reader.ReadLineAsync());
        Assert.Contains("data: Event 2", await reader.ReadToEndAsync());

        using var socket  = new ClientWebSocket();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await socket.ConnectAsync(new Uri($"ws://localhost:{client.BaseAddress!.Port}/api/socket"), timeout.Token);
        await socket.SendAsync("hello"u8.ToArray().AsMemory(), WebSocketMessageType.Text, true, timeout.Token);
        var buffer  = new byte[128];
        var message = await socket.ReceiveAsync(buffer.AsMemory(), timeout.Token);
        Assert.Equal("hello", Encoding.UTF8.GetString(buffer, 0, message.Count));
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", timeout.Token);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HttpListenerShutdownDoesNotFaultPendingAccepts(bool disposeWithoutStopping)
    {
        for (var iteration = 0; iteration < 10; iteration++)
        {
            var port    = FreePort(SocketType.Stream, ProtocolType.Tcp);
            var builder = Host.CreateApplicationBuilder();
            builder.Logging.ClearProviders();
            builder.Services.AddServal().AddHttpListener(options =>
            {
                options.Prefixes.Clear();
                options.Prefixes.Add($"http://localhost:{port}/");
            });
            using var host = builder.Build();
            await host.StartAsync();
            var service = Assert.Single(host.Services.GetServices<IHostedService>().OfType<BackgroundService>());
            await Task.Yield();
            if (disposeWithoutStopping)
            {
                host.Dispose();
            }
            else
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await host.StopAsync(timeout.Token);
                await host.StopAsync(timeout.Token);
            }

            await service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(service.ExecuteTask.IsCompletedSuccessfully);
        }
    }

    [Fact]
    public async Task HttpListenerAsyncDisposalWaitsForActiveRequestCleanup()
    {
        var port    = FreePort(SocketType.Stream, ProtocolType.Tcp);
        var handler = new PendingShutdownHandler();
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddServal().AddHttpListener(options =>
        {
            options.Prefixes.Clear();
            options.Prefixes.Add($"http://localhost:{port}/");
        });
        builder.Services.AddSingleton<IRequestHandler>(handler);
        var                        host     = builder.Build();
        using var                  client   = new HttpClient(ClientHandler, false);
        Task?                      disposal = null;
        Task<HttpResponseMessage>? pending  = null;
        try
        {
            await host.StartAsync();
            pending = client.GetAsync($"http://localhost:{port}/");
            await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            disposal = ((IAsyncDisposable)host).DisposeAsync().AsTask();
            await handler.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(disposal.IsCompleted);
            Assert.False(handler.Finished);
        }
        finally
        {
            handler.Release.TrySetResult();
            await (disposal ?? ((IAsyncDisposable)host).DisposeAsync().AsTask()).WaitAsync(TimeSpan.FromSeconds(5));
            if (pending is not null)
            {
                // Stopping the listener aborts its sockets while handler cleanup drains.
                _ = await Record.ExceptionAsync(async () =>
                {
                    using var response = await pending.WaitAsync(TimeSpan.FromSeconds(5));
                });
            }
        }

        Assert.True(handler.Finished);
    }

    [QuicFact]
    public async Task Http3ServesConcurrentRequestsJsonBodiesAndErrors()
    {
        await using var server = await TransportTestHost.StartAsync(HttpVersion.Version30, true, ClientHandler,
            serval => Serval_Sample_TransportsControllers.Register(serval));
        var       client   = server.Client;
        using var response = await client.GetAsync("/test/items/12");
        Assert.Equal(HttpVersion.Version30, response.Version);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("12:controller,method", await response.Content.ReadAsStringAsync());
        var pending = new Task<string>[20];
        for (var index = 0; index < pending.Length; index++)
        {
            pending[index] = client.GetStringAsync($"/test/items/{index + 1}");
        }

        var responses = await Task.WhenAll(pending);
        Assert.Equal(20, responses.Length);
        using var content = new StringContent("{\"Text\":\"quic\"}", Encoding.UTF8, MimeTypes.Json);
        using var post    = await client.PostAsync("/test/json", content);
        Assert.Equal("{\"Text\":\"quic\"}", await post.Content.ReadAsStringAsync());
        using var error = await client.GetAsync("/test/error");
        Assert.Equal((HttpStatusCode)StatusCodes.UnprocessableContent, error.StatusCode);
        Assert.Contains("validation_failed", await error.Content.ReadAsStringAsync());

        using var head = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/test/items/12")
        {
            Version       = HttpVersion.Version30,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        });
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.Empty(await head.Content.ReadAsByteArrayAsync());
        using var       events    = await client.GetAsync("/api/events", HttpCompletionOption.ResponseHeadersRead);
        await using var eventBody = await events.Content.ReadAsStreamAsync();
        using var       reader    = new StreamReader(eventBody);
        Assert.Equal("event: tick", await reader.ReadLineAsync());
        Assert.Contains("data: Event 2", await reader.ReadToEndAsync());
    }

    private static int FreePort(SocketType type, ProtocolType protocol)
    {
        using var socket = new Socket(AddressFamily.InterNetwork, type, protocol);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }

    private sealed class PendingShutdownHandler : IRequestHandler
    {
        public TaskCompletionSource Entered   { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release   { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool                 Finished  { get; private set; }

        public async ValueTask HandleAsync(Request request, Response response, CancellationToken cancellationToken)
        {
            await using var registration = cancellationToken.Register(() => Cancelled.TrySetResult());
            Entered.TrySetResult();
            await Release.Task;
            Finished = true;
        }
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
