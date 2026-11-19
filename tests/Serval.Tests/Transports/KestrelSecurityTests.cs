#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Serval.Tests.Support;

namespace Serval.Tests.Transports;

public sealed class KestrelSecurityTests
{
    [Theory]
    [InlineData("/test/items/%31", "1:controller,method")]
    [InlineData("/test/files/a%2Fb%20c", "a/b c")]
    [InlineData("/test/files/100%25", "100%")]
    [InlineData("/test/files/caf%C3%A9", "café")]
    public async Task UnambiguousEscapedPathsRemainUsable(string target, string expected)
    {
        using var handler = new SocketsHttpHandler { UseProxy = false };
        await using var host = await TransportTestHost.StartAsync(HttpVersion.Version11, false, handler, kestrel: true);
        using var response = await host.Client.GetAsync(target);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expected, await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("/public/%252e%252e/admin")]
    [InlineData("/public/%2e%2e%5cadmin")]
    [InlineData("/public/..%5cadmin")]
    public async Task PathNormalizationCannotBypassUpstreamAuthorization(string target)
    {
        using var handler = new SocketsHttpHandler { UseProxy = false };
        var dispatched = false;
        await using var host = await TransportTestHost.StartAsync(HttpVersion.Version11, false, handler,
            serval => serval.AddEndpoint("GET", "/admin", context =>
            {
                dispatched = true;
                return context.Response.WriteTextAsync("private");
            }), kestrel: true, configureApplication: application => application.Use((context, next) =>
            {
                if (context.Request.Path.StartsWithSegments("/admin"))
                {
                    context.Response.StatusCode = 403;
                    return Task.CompletedTask;
                }

                return next(context);
            }));

        var response = await SendRawAsync(host, $"GET {target} HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n");
        Assert.False(dispatched, response);
        Assert.DoesNotContain("private", response);
    }

    private static async Task<string> SendRawAsync(TransportTestHost host, string request)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, host.Client.BaseAddress!.Port, cts.Token);
        await using var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request), cts.Token);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync(cts.Token);
    }
}
