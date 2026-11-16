using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;


namespace Serval.SocketIO.Tests;

public sealed class TransportTests
{
    [Theory]
    [InlineData("?EIO=3&transport=polling")]
    [InlineData("?EIO=4&EIO=4&transport=polling")]
    [InlineData("?EIO=4&transport=unknown")]
    [InlineData("?EIO=4&transport=polling&sid=unknown")]
    [InlineData("?EIO=4&transport=polling&sid=")]
    [InlineData("?transport=polling")]
    public async Task RejectsInvalidHandshake(string query)
    {
        await using var server   = await TestServer.StartAsync();
        using var       client   = new HttpClient();
        using var       response = await client.GetAsync(server.Url + "/socket.io/" + query);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, server.Server.ConnectionCount);
    }

    [Fact]
    public async Task OriginsAreCheckedForPollingAndWebSockets()
    {
        await using var server   = await TestServer.StartAsync();
        using var       client   = new HttpClient();
        using var       rejected = new HttpRequestMessage(HttpMethod.Get, server.Url + "/socket.io/?EIO=4&transport=polling");
        rejected.Headers.Add("Origin", "https://untrusted.example");
        using var response = await client.SendAsync(rejected);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var allowed = new HttpRequestMessage(HttpMethod.Options, server.Url + "/socket.io/");
        allowed.Headers.Add("Origin", "https://allowed.example");
        using var preflight = await client.SendAsync(allowed);
        Assert.Equal(HttpStatusCode.NoContent, preflight.StatusCode);
        Assert.Equal("https://allowed.example", preflight.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Equal("true", preflight.Headers.GetValues("Access-Control-Allow-Credentials").Single());
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Origin", "https://untrusted.example");
        await Assert.ThrowsAsync<WebSocketException>(() => socket.ConnectAsync(new Uri(server.Url.Replace("http:", "ws:") + "/socket.io/?EIO=4&transport=websocket"), CancellationToken.None));
        Assert.Equal(0, server.Server.ConnectionCount);
    }

    [Theory]
    [InlineData("4invalid")]
    [InlineData("bAQI=")]
    [InlineData("b!!")]
    [InlineData("42[\"event before connect\"]")]
    [InlineData("451-[\"binary\",{\"_placeholder\":true,\"num\":2}]")]
    public async Task InvalidPayloadClosesItsSession(string payload)
    {
        await using var server = await TestServer.StartAsync();
        using var       client = new HttpClient();
        var             sid    = await HandshakeAsync(client, server.Url);
        using var       posted = await client.PostAsync(PollUrl(server.Url, sid), new StringContent(payload));
        Assert.Contains(posted.StatusCode, new[] { HttpStatusCode.OK, HttpStatusCode.BadRequest });
        await WaitUntilAsync(() => server.Server.ConnectionCount == 0);
        using var response = await client.GetAsync(PollUrl(server.Url, sid));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task HeartbeatTimeoutReleasesAnUnresponsiveClient()
    {
        await using var server = await TestServer.StartAsync(options =>
        {
            options.PingTimeout = TimeSpan.FromMilliseconds(200);
        });
        using var client    = new HttpClient();
        var       sid       = await HandshakeAsync(client, server.Url);
        using var connected = await client.PostAsync(PollUrl(server.Url, sid), new StringContent("40"));
        Assert.StartsWith("40", await PollApplicationAsync(client, server.Url, sid));
        Assert.Equal("2", await client.GetStringAsync(PollUrl(server.Url, sid)));
        await WaitUntilAsync(() => server.Server.ConnectionCount == 0);
        Assert.NotEmpty(server.Scopes.Disconnected);
    }

    [Fact]
    public async Task MissingBinaryAttachmentsExpireEvenWhenHeartbeatRepliesContinue()
    {
        await using var server  = await TestServer.StartAsync(options => options.PayloadTimeout = TimeSpan.FromMilliseconds(500));
        using var       client  = new HttpClient();
        var             sid     = await HandshakeAsync(client, server.Url);
        using var       connect = await client.PostAsync(PollUrl(server.Url, sid), new StringContent("40"));
        Assert.StartsWith("40", await PollApplicationAsync(client, server.Url, sid));
        using var incomplete = await client.PostAsync(PollUrl(server.Url, sid), new StringContent("451-[\"binary\",{\"_placeholder\":true,\"num\":0}]"));
        using var timeout    = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (server.Server.ConnectionCount > 0)
        {
            using var response = await client.GetAsync(PollUrl(server.Url, sid), timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                break;
            }

            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            if (body == "2")
            {
                using var pong = await client.PostAsync(PollUrl(server.Url, sid), new StringContent("3"), timeout.Token);
            }
        }

        await WaitUntilAsync(() => server.Server.ConnectionCount == 0);
    }

    [Fact]
    public async Task ConnectionLimitAndPollingOnlyConfigurationAreEnforced()
    {
        await using var server = await TestServer.StartAsync(options =>
        {
            options.MaxConnections   = 1;
            options.EnableWebSockets = false;
        });
        using var client   = new HttpClient();
        var       body     = await client.GetStringAsync(server.Url + "/socket.io/?EIO=4&transport=polling");
        using var document = JsonDocument.Parse(body[1..]);
        Assert.Empty(document.RootElement.GetProperty("upgrades").EnumerateArray());
        using var full = await client.GetAsync(server.Url + "/socket.io/?EIO=4&transport=polling");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, full.StatusCode);
    }

    [Fact]
    public async Task ConcurrentPollingGetsCloseTheSession()
    {
        await using var server    = await TestServer.StartAsync(options => options.PingInterval = TimeSpan.FromSeconds(10));
        using var       client    = new HttpClient();
        var             sid       = await HandshakeAsync(client, server.Url);
        using var       connected = await client.PostAsync(PollUrl(server.Url, sid), new StringContent("40"));
        Assert.StartsWith("40", await PollApplicationAsync(client, server.Url, sid));
        using var timeout   = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var       responses = await Task.WhenAll(client.GetAsync(PollUrl(server.Url, sid), timeout.Token), client.GetAsync(PollUrl(server.Url, sid), timeout.Token));
        try
        {
            Assert.Contains(responses, response => response.StatusCode == HttpStatusCode.BadRequest);
            await WaitUntilAsync(() => server.Server.ConnectionCount == 0);
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueueByteLimitsCloseOverloadedSessions(bool outgoing)
    {
        await using var server    = await TestServer.StartAsync(options => options.MaxQueuedBytes = 128);
        using var       client    = new HttpClient();
        var             sid       = await HandshakeAsync(client, server.Url);
        using var       connected = await client.PostAsync(PollUrl(server.Url, sid), new StringContent("40"));
        Assert.StartsWith("40", await PollApplicationAsync(client, server.Url, sid));
        var       payload = outgoing ? "421[\"server-binary\"]" : "421[\"echo\",\"" + new string('x', 200) + "\"]";
        using var sent    = await client.PostAsync(PollUrl(server.Url, sid), new StringContent(payload));
        await WaitUntilAsync(() => server.Server.ConnectionCount == 0);
    }

    [Fact]
    public async Task IncompletePostTimesOutWithoutHoldingAConnectionSlot()
    {
        await using var server = await TestServer.StartAsync(options =>
        {
            options.PayloadTimeout = TimeSpan.FromMilliseconds(200);
            options.PingInterval   = TimeSpan.FromSeconds(30);
            options.ConnectTimeout = TimeSpan.FromSeconds(30);
        });
        using var client  = new HttpClient();
        var       sid     = await HandshakeAsync(client, server.Url);
        using var tcp     = new System.Net.Sockets.TcpClient();
        var       uri     = new Uri(PollUrl(server.Url, sid));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await tcp.ConnectAsync("localhost", uri.Port, timeout.Token);
        var bytes = Encoding.ASCII.GetBytes($"POST {uri.PathAndQuery} HTTP/1.1\r\nHost: localhost:{uri.Port}\r\nContent-Type: text/plain\r\nContent-Length: 100\r\n\r\n4");
        await tcp.GetStream().WriteAsync(bytes, timeout.Token);
        await WaitUntilAsync(() => server.Server.ConnectionCount == 0);
    }

    [Fact]
    public async Task DuplicatePostCancelsTheOutstandingBodyRead()
    {
        await using var server  = await TestServer.StartAsync(options => options.PingInterval = TimeSpan.FromSeconds(10));
        using var       client  = new HttpClient();
        var             sid     = await HandshakeAsync(client, server.Url);
        using var       tcp     = new System.Net.Sockets.TcpClient();
        var             uri     = new Uri(PollUrl(server.Url, sid));
        using var       timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await tcp.ConnectAsync("localhost", uri.Port, timeout.Token);
        var bytes = Encoding.ASCII.GetBytes($"POST {uri.PathAndQuery} HTTP/1.1\r\nHost: localhost:{uri.Port}\r\nContent-Type: text/plain\r\nContent-Length: 100\r\n\r\n4");
        await tcp.GetStream().WriteAsync(bytes, timeout.Token);
        // A second deliberately incomplete POST guarantees an overlap regardless of request scheduling.
        using var second = new System.Net.Sockets.TcpClient();
        await second.ConnectAsync("localhost", uri.Port, timeout.Token);
        await second.GetStream().WriteAsync(bytes, timeout.Token);
        await WaitUntilAsync(() => server.Server.ConnectionCount == 0);
    }

    [Fact]
    public async Task FragmentedWebSocketPacketsAreReassembledAndOversizedPacketsClose()
    {
        await using var server  = await TestServer.StartAsync();
        using var       socket  = new ClientWebSocket();
        using var       timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await socket.ConnectAsync(new Uri(server.Url.Replace("http:", "ws:") + "/socket.io/?EIO=4&transport=websocket"), timeout.Token);
        Assert.StartsWith("0", await ReceiveTextAsync(socket, timeout.Token));
        await socket.SendAsync("4"u8.ToArray().AsMemory(), WebSocketMessageType.Text, false, timeout.Token);
        await socket.SendAsync("0"u8.ToArray().AsMemory(), WebSocketMessageType.Text, true, timeout.Token);
        Assert.StartsWith("40", await ReceiveTextAsync(socket, timeout.Token));
        await socket.SendAsync(new byte[32769].AsMemory(), WebSocketMessageType.Binary, true, timeout.Token);
        await WaitUntilAsync(() => server.Server.ConnectionCount == 0);
    }

    [Fact]
    public async Task AbandonedUpgradeKeepsPollingUsable()
    {
        await using var server    = await TestServer.StartAsync();
        using var       client    = new HttpClient();
        var             sid       = await HandshakeAsync(client, server.Url);
        using var       connected = await client.PostAsync(PollUrl(server.Url, sid), new StringContent("40"));
        Assert.StartsWith("40", await PollApplicationAsync(client, server.Url, sid));
        using var socket  = new ClientWebSocket();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await socket.ConnectAsync(new Uri(server.Url.Replace("http:", "ws:") + $"/socket.io/?EIO=4&transport=websocket&sid={sid}"), timeout.Token);
        await socket.SendAsync("2probe"u8.ToArray().AsMemory(), WebSocketMessageType.Text, true, timeout.Token);
        Assert.Equal("3probe", await ReceiveTextAsync(socket, timeout.Token));
        socket.Abort();
        using var echo = await client.PostAsync(PollUrl(server.Url, sid), new StringContent("421[\"echo\",\"still polling\"]"));
        Assert.Equal("431[\"still polling\"]", await PollApplicationAsync(client, server.Url, sid));
    }

    [Fact]
    public async Task ShutdownCancelsHandlersAndDisposesTheirScopes()
    {
        await using var server  = await TestServer.StartAsync();
        using var       client  = new HttpClient();
        var             sid     = await HandshakeAsync(client, server.Url);
        using var       connect = await client.PostAsync(PollUrl(server.Url, sid), new StringContent("40"));
        Assert.StartsWith("40", await PollApplicationAsync(client, server.Url, sid));
        using var wait = await client.PostAsync(PollUrl(server.Url, sid), new StringContent("42[\"wait\"]"));
        await WaitUntilAsync(() => server.Scopes.Scopes.Values.Any(disposed => !disposed));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await server.Host.StopAsync(timeout.Token);
        Assert.Equal(0, server.Server.ConnectionCount);
        Assert.All(server.Scopes.Scopes.Values, disposed => Assert.True(disposed));
        Assert.Single(server.Scopes.Disconnected);
    }

    [Fact]
    public async Task SampleServesItsPageAndPinnedOfficialClient()
    {
        var       port = TestServer.FreePort();
        using var host = Sample.SocketIO.Program.Build(["--port", port.ToString(), "--Logging:LogLevel:Default", "None"]);
        await host.StartAsync();
        try
        {
            using var client = new HttpClient();
            Assert.Contains("Socket.IO with Serval", await client.GetStringAsync($"http://localhost:{port}/"));
            Assert.Contains("Socket.IO v4.8.4", await client.GetStringAsync($"http://localhost:{port}/socketio/socket.io.min.js"));
        }
        finally
        {
            await host.StopAsync();
        }
    }

    private static async Task<string> HandshakeAsync(HttpClient client, string url)
    {
        var body = await client.GetStringAsync(url + "/socket.io/?EIO=4&transport=polling");
        Assert.StartsWith("0", body);
        using var document = JsonDocument.Parse(body[1..]);
        return document.RootElement.GetProperty("sid").GetString()!;
    }

    private static string PollUrl(string url, string sid) => url + "/socket.io/?EIO=4&transport=polling&sid=" + sid;

    private static async Task<string> PollApplicationAsync(HttpClient client, string url, string sid)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            var payload = await client.GetStringAsync(PollUrl(url, sid), timeout.Token);
            string? application = null;
            foreach (var packet in payload.Split('\x1e'))
            {
                if (packet == "2")
                {
                    using var pong = await client.PostAsync(PollUrl(url, sid), new StringContent("3"), timeout.Token);
                    Assert.True(pong.IsSuccessStatusCode);
                }
                else if (packet != "6")
                {
                    application = packet;
                }
            }

            if (application is not null) { return application; }
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            await Task.Delay(20, timeout.Token);
        }
    }

    private static async Task<string> ReceiveTextAsync(ClientWebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[4096];
        while (true)
        {
            var received = await socket.ReceiveAsync(buffer.AsMemory(), ct);
            var text     = Encoding.UTF8.GetString(buffer, 0, received.Count);
            if (text != "2")
            {
                return text;
            }

            await socket.SendAsync("3"u8.ToArray().AsMemory(), WebSocketMessageType.Text, true, ct);
        }
    }
}
