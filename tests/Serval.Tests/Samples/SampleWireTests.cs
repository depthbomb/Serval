#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using Serval.Tests.Support;

namespace Serval.Tests.Samples;

public sealed class SampleWireTests
{
    [Theory]
    [InlineData("Routing")]
    [InlineData("Savanna")]
    [InlineData("Vite")]
    [InlineData("TodoApi")]
    [InlineData("Middleware")]
    [InlineData("Lifecycles")]
    [InlineData("Files")]
    [InlineData("Forms")]
    [InlineData("Streaming")]
    [InlineData("CookieAuth")]
    [InlineData("BearerAuth")]
    [InlineData("OAuth")]
    [InlineData("SignedUrls")]
    [InlineData("Exceptions")]
    public async Task StandaloneSamplesStartAndServeTheirIndex(string name)
    {
        var       port    = FreePort();
        using var host    = SampleApplication.Build(name, ["--port", port.ToString(CultureInfo.InvariantCulture)]);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await host.StartAsync(timeout.Token);
        try
        {
            using var client   = new HttpClient(new SocketsHttpHandler { UseProxy = false });
            using var response = await client.GetAsync($"http://localhost:{port}/", timeout.Token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.NotEmpty(await response.Content.ReadAsStringAsync(timeout.Token));
            using var head = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, $"http://localhost:{port}/"), timeout.Token);
            Assert.Equal(HttpStatusCode.OK, head.StatusCode);
            Assert.Empty(await head.Content.ReadAsByteArrayAsync(timeout.Token));
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task StreamingSampleEchoesFragmentsAndClosesOversizedMessages()
    {
        var       port    = FreePort();
        using var host    = SampleApplication.Build("Streaming", ["--port", port.ToString(CultureInfo.InvariantCulture)]);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await host.StartAsync(timeout.Token);
        try
        {
            using var socket = new ClientWebSocket();
            await socket.ConnectAsync(new Uri($"ws://localhost:{port}/socket"), timeout.Token);
            await socket.SendAsync("hello "u8.ToArray().AsMemory(), WebSocketMessageType.Text, false, timeout.Token);
            await socket.SendAsync("world"u8.ToArray().AsMemory(), WebSocketMessageType.Text, true, timeout.Token);
            var                         buffer = new byte[8192];
            using var                   echoed = new MemoryStream();
            ValueWebSocketReceiveResult message;
            do
            {
                message = await socket.ReceiveAsync(buffer.AsMemory(), timeout.Token);
                echoed.Write(buffer, 0, message.Count);
            } while (!message.EndOfMessage);

            Assert.Equal("hello world", Encoding.UTF8.GetString(echoed.ToArray()));

            // Read echoed fragments while sending so the test cannot fill either peer's send buffer.
            var receive = Task.Run(async () =>
            {
                while (true)
                {
                    var frame = await socket.ReceiveAsync(buffer.AsMemory(), timeout.Token);
                    if (frame.MessageType == WebSocketMessageType.Close)
                    {
                        return socket.CloseStatus;
                    }
                }
            }, timeout.Token);
            var oversized = new byte[64 * 1024 + 1];
            await socket.SendAsync(oversized.AsMemory(), WebSocketMessageType.Binary, true, timeout.Token);
            Assert.Equal(WebSocketCloseStatus.MessageTooBig, await receive);
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", timeout.Token);
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
        }
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
