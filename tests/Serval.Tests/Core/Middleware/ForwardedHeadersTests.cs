using Microsoft.Extensions.DependencyInjection;
using System.Net;
using Serval.Hosting;
using Serval.Http;
using Serval.Middleware;
using Serval.Tests.Support;

namespace Serval.Tests.Core.Middleware;

public sealed class ForwardedHeadersTests
{
    [Theory]
    [InlineData("10.0.0.1", true)]
    [InlineData("::ffff:10.0.0.1", true)]
    [InlineData("192.0.2.10", false)]
    public async Task OnlyTrustedPeersCanChangePublicRequestInformation(string peer, bool trusted)
    {
        await using var app      = Application();
        await using var request  = Request(peer, "203.0.113.1", "https", "app.test:8443", "/portal");
        var             original = request.Url;
        var             response = new MemoryResponse();
        await app.Services.GetRequiredService<IRequestHandler>().HandleAsync(request, response, default);
        Assert.Equal(404, response.StatusCode);
        Assert.Same(original, request.Url);
        Assert.Equal(peer, request.Connection.RemoteAddress!.ToString());
        Assert.Equal(trusted ? "203.0.113.1" : peer, request.ClientAddress!.ToString());
        Assert.Equal(trusted ? "https://app.test:8443/portal/missing?x=1" : "http://internal/missing?x=1", request.PublicUrl.AbsoluteUri);
    }

    [Theory]
    [InlineData(1, "10.0.0.2", "edge.test")]
    [InlineData(2, "203.0.113.1", "app.test")]
    public async Task ChainsAreConsumedFromTheRightUpToTheConfiguredLimit(int limit, string address, string host)
    {
        await using var app = Application(options =>
        {
            options.KnownNetworks.Add(IPNetwork.Parse("10.0.0.0/24"));
            options.ForwardLimit = limit;
        });
        await using var request = Request("10.0.0.1", "203.0.113.1, 10.0.0.2", "https, http", "app.test, edge.test");
        await app.Services.GetRequiredService<IRequestHandler>().HandleAsync(request, new MemoryResponse(), default);
        Assert.Equal(address, request.ClientAddress!.ToString());
        Assert.Equal(host, request.Host);
    }

    [Fact]
    public async Task UntrustedHopStopsConsumptionOfSpoofedEarlierValues()
    {
        await using var app      = Application(options => options.ForwardLimit = 5);
        await using var request  = Request("10.0.0.1", "bad, 203.0.113.1", "bad, https", "bad@host, app.test");
        var             response = new MemoryResponse();
        await app.Services.GetRequiredService<IRequestHandler>().HandleAsync(request, response, default);
        Assert.Equal(404, response.StatusCode);
        Assert.Equal("203.0.113.1", request.ClientAddress!.ToString());
        Assert.Equal("app.test", request.Host);
    }

    [Theory]
    [InlineData("bad", "https", "app.test", "/", 400)]
    [InlineData("203.0.113.1", "ftp", "app.test", "/", 400)]
    [InlineData("203.0.113.1", "https, http", "app.test", "/", 400)]
    [InlineData("203.0.113.1", "https", "user@app.test", "/", 400)]
    [InlineData("203.0.113.1", "https", "app.test", "//evil.test", 400)]
    [InlineData("203.0.113.1", "https", "app.test", "/one/../two", 400)]
    [InlineData("203.0.113.1", "https", "app.test", "/one?two", 400)]
    [InlineData("203.0.113.1", "https", "app.test", "/one/", 404)]
    public async Task InvalidForwardedValuesAreRejectedAtomically(string address, string scheme, string host, string prefix, int status)
    {
        await using var app      = Application();
        await using var request  = Request("10.0.0.1", address, scheme, host, prefix);
        var             response = new MemoryResponse();
        await app.Services.GetRequiredService<IRequestHandler>().HandleAsync(request, response, default);
        Assert.Equal(status, response.StatusCode);
        if (status == 400)
        {
            Assert.Equal("10.0.0.1", request.ClientAddress!.ToString());
            Assert.Equal("internal", request.Host);
        }
    }

    [Fact]
    public async Task AllowedHostsAreEnforcedAndConfigurationIsCopied()
    {
        ForwardedHeadersOptions? configured = null;
        await using var app = Application(options =>
        {
            configured = options;
            options.AllowedHosts.Add("app.test");
        });
        configured!.AllowedHosts.Clear();
        await using var request  = Request("10.0.0.1", "203.0.113.1", "https", "other.test");
        var             response = new MemoryResponse();
        await app.Services.GetRequiredService<IRequestHandler>().HandleAsync(request, response, default);
        Assert.Equal(400, response.StatusCode);
    }

    private static TestApplication Application(Action<ForwardedHeadersOptions>? configure = null)
        => new(configureBuilder: builder => builder.UseForwardedHeaders(options =>
        {
            options.KnownProxies.Add(IPAddress.Parse("10.0.0.1"));
            configure?.Invoke(options);
        }));

    private static Request Request(string peer, string address, string scheme, string host, string? prefix = null)
        => new("GET", new Uri("http://internal/missing?x=1"), HttpVersion.Version11, new Headers
        {
            ["X-Forwarded-For"]    = address,
            ["X-Forwarded-Proto"]  = scheme,
            ["X-Forwarded-Host"]   = host,
            ["X-Forwarded-Prefix"] = prefix,
        }, Stream.Null, 1024, new RequestConnection(new IPEndPoint(IPAddress.Parse(peer), 12345)));
}
