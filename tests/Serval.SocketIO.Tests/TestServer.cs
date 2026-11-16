using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serval.Authentication;
using Serval.Hosting;
using Serval.Transport.HttpListener;

namespace Serval.SocketIO.Tests;

internal sealed class TestServer : IAsyncDisposable
{
    internal IHost          Host   { get; }
    internal string         Url    { get; }
    internal SocketIOServer Server => Host.Services.GetRequiredService<SocketIOServer>();
    internal ScopeTracker   Scopes => Host.Services.GetRequiredService<ScopeTracker>();

    private TestServer(IHost host, string url)
    {
        Host = host;
        Url  = url;
    }

    public async ValueTask DisposeAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Host.StopAsync(timeout.Token);
        Host.Dispose();
    }

    internal static async Task<TestServer> StartAsync(Action<SocketIOOptions>? configure = null)
    {
        var port    = FreePort();
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<ScopeTracker>();
        builder.Services.AddScoped<ScopedMarker>();
        var serval = builder.Services.AddServal();
        serval.AddAuthentication(options => options.DefaultScheme = "test").AddBearer("test", options =>
        {
            options.RequireHttps = false;
            options.ValidateToken = (_, token) => ValueTask.FromResult(token == "http-test"
                ? AuthenticationResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "http-user")], "test")), "test", new AuthenticationProperties()))
                : AuthenticationResult.Fail("Invalid test token."));
        });
        serval.Use(async (context, next) =>
        {
            await context.AuthenticateAsync();
            await next(context);
        }).AddSocketIO(socketIo =>
        {
            socketIo.MapNamespace<TestHandler>("/").MapNamespace<TestHandler>("/chat").MapNamespace<TestHandler>("/private");
        }, options =>
        {
            options.PingInterval    = TimeSpan.FromMilliseconds(200);
            options.PingTimeout     = TimeSpan.FromSeconds(2);
            options.ConnectTimeout  = TimeSpan.FromSeconds(3);
            options.UpgradeTimeout  = TimeSpan.FromSeconds(1);
            options.HandlerTimeout  = TimeSpan.FromSeconds(5);
            options.AckTimeout      = TimeSpan.FromSeconds(1);
            options.MaxPayloadBytes = 32768;
            options.MaxQueuedBytes  = 131072;
            options.AllowedOrigins.Add("https://allowed.example");
            configure?.Invoke(options);
        }).AddHttpListener(options =>
        {
            options.Prefixes.Clear();
            options.Prefixes.Add($"http://localhost:{port}/");
        });
        var host = builder.Build();
        await host.StartAsync();
        return new TestServer(host, $"http://localhost:{port}");
    }

    internal static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}

internal sealed class ScopeTracker
{
    internal ConcurrentDictionary<string, bool> Scopes       { get; } = new();
    internal ConcurrentQueue<string>            Disconnected { get; } = new();
    internal ConcurrentQueue<string>            Waiting { get; } = new();
}

internal sealed class ScopedMarker : IAsyncDisposable
{
    internal         string       Id { get; } = Guid.NewGuid().ToString("N");
    private readonly ScopeTracker _tracker;

    public ScopedMarker(ScopeTracker tracker)
    {
        _tracker           = tracker;
        tracker.Scopes[Id] = false;
    }

    public ValueTask DisposeAsync()
    {
        _tracker.Scopes[Id] = true;
        return ValueTask.CompletedTask;
    }
}

internal sealed class TestHandler : SocketIOHandler
{
    private readonly SocketIOServer _server;
    private readonly ScopedMarker   _scope;
    private readonly ScopeTracker   _tracker;

    public TestHandler(SocketIOServer server, ScopedMarker scope, ScopeTracker tracker)
    {
        _server  = server;
        _scope   = scope;
        _tracker = tracker;
    }

    public override ValueTask<bool> OnConnectingAsync(SocketIOConnection socket, JsonElement auth, CancellationToken ct)
    {
        if (socket.Namespace != "/private")
        {
            return ValueTask.FromResult(true);
        }

        var accepted = auth.ValueKind == JsonValueKind.Object && auth.TryGetProperty("token", out var token) && token.GetString() == "namespace-test";
        if (accepted)
        {
            socket.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "namespace-user")], "namespace"));
        }

        return ValueTask.FromResult(accepted);
    }

    public override async ValueTask OnEventAsync(SocketIOEvent message, CancellationToken ct)
    {
        switch (message.Name)
        {
            case "echo":
                await message.AcknowledgeAsync([.. Enumerable.Range(0, message.Arguments.Count).Select(index => (object?)message.Arguments[index])]);
                break;
            case "binary":
                await message.AcknowledgeAsync(message.Arguments.Get<BinaryEnvelope>(0));
                break;
            case "server-binary":
                await message.Socket.EmitAsync("binary-event", new BinaryEnvelope
                {
                    Data   = [0, 1, 255],
                    Nested = [[7, 8], []],
                });
                await message.AcknowledgeAsync("sent");
                break;
            case "join":
                message.Socket.Join(message.Arguments.Get<string>(0)!);
                await message.AcknowledgeAsync(message.Socket.Rooms);
                break;
            case "leave":
                message.Socket.Leave(message.Arguments.Get<string>(0)!);
                await message.AcknowledgeAsync(message.Socket.Rooms);
                break;
            case "broadcast":
                await _server.BroadcastAsync(message.Socket.Namespace, "broadcast", [message.Arguments[1]], message.Arguments.Get<string>(0), message.Socket.Id);
                await message.AcknowledgeAsync("sent");
                break;
            case "ask":
            {
                var reply = await message.Socket.EmitWithAcknowledgementAsync("question", ["answer me"], ct: ct);
                await message.AcknowledgeAsync(reply.Get<BinaryEnvelope>(0));
                break;
            }
            case "ack-timeout":
                try
                {
                    await message.Socket.EmitWithAcknowledgementAsync("ignored", [], TimeSpan.FromMilliseconds(100), ct);
                }
                catch (TimeoutException)
                {
                    await message.AcknowledgeAsync("timed out");
                }
                break;
            case "scope":
                await message.AcknowledgeAsync(new
                {
                    id               = _scope.Id,
                    previousDisposed = message.Arguments.Count > 0 && _tracker.Scopes.GetValueOrDefault(message.Arguments.Get<string>(0)!),
                });
                break;
            case "user":
                await message.AcknowledgeAsync(message.Socket.User.Identity?.Name);
                break;
            case "disconnect": // Reserved on the wire; applications use the command below instead.
                throw new InvalidOperationException();
            case "close-namespace":
                await message.AcknowledgeAsync("closing");
                message.Socket.Disconnect();
                break;
            case "close-transport":
                message.Socket.Disconnect(closeTransport: true);
                break;
            case "wait":
                _tracker.Waiting.Enqueue(message.Socket.Id);
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                break;
        }
    }

    public override ValueTask OnDisconnectedAsync(SocketIOConnection socket, string reason, CancellationToken ct)
    {
        _tracker.Disconnected.Enqueue(socket.Id);
        return ValueTask.CompletedTask;
    }
}

internal sealed class BinaryEnvelope
{
    public byte[]   Data   { get; set; } = [];
    public byte[][] Nested { get; set; } = [];
}
