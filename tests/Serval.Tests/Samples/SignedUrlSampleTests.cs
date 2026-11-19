#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using Serval.Http.Constants;
using Serval.SignedUrls;
using Serval.Tests.Support;

namespace Serval.Tests.Samples;

public sealed class SignedUrlSampleTests
{
    // Public fixture data, never an application signing key.
    private static readonly string Key = Convert.ToBase64String(Enumerable.Range(0, 32).Select(value => (byte)value).ToArray());

    [Fact]
    public async Task GeneratedLinksWorkWithMiddlewareAndExposeVerifiedInspectionData()
    {
        await using var app      = new SampleApplication("SignedUrls", "--SignedUrls:Key", Key);
        var             signer   = app.Host.Services.GetRequiredService<ISignedUrlService>();
        var             response = await app.SendAsync("/links");
        Assert.Equal(200, response.StatusCode);
        Assert.Equal("no-store", response.Headers[HeaderNames.CacheControl]);
        var links = Json(response);
        Assert.Equal(30, links.GetProperty("seconds").GetInt32());
        foreach (var name in new[] { "permanent", "temporary", "typed" })
        {
            var url      = links.GetProperty(name).GetString()!;
            var download = await app.SendAsync(url);
            Assert.Equal(200, download.StatusCode);
            Assert.Equal("no-store", download.Headers[HeaderNames.CacheControl]);
            var report = Json(download);
            Assert.Equal(42, report.GetProperty("id").GetInt32());
            Assert.Equal("pdf", report.GetProperty("format").GetString());
            Assert.Equal("Monthly report", report.GetProperty("title").GetString());

            var data = signer.Validate(url).Data!;
            Assert.Equal(name == "permanent", data.ExpiresAt is null);
            Assert.Equal(name == "typed" ? null : "downloads.show", data.RouteName);
        }

        var inspection = await app.SendAsync(links.GetProperty("inspection").GetString()!);
        Assert.Equal(200, inspection.StatusCode);
        var result = Json(inspection);
        Assert.True(result.GetProperty("isValid").GetBoolean());
        Assert.Equal("None", result.GetProperty("failure").GetString());
        var verified = result.GetProperty("data");
        Assert.Equal("downloads.inspect", verified.GetProperty("routeName").GetString());
        Assert.Equal("42", verified.GetProperty("routeValues").GetProperty("id").GetString());
        Assert.Equal("Monthly report", verified.GetProperty("query").GetProperty("title")[0].GetString());
        Assert.True(verified.GetProperty("expiresAt").TryGetDateTimeOffset(out _));
    }

    [Fact]
    public async Task MissingAndTamperedLinksAreRejectedWithoutExposingPayloadData()
    {
        await using var app      = new SampleApplication("SignedUrls", "--SignedUrls:Key", Key);
        var             links    = Json(await app.SendAsync("/links"));
        var             download = links.GetProperty("permanent").GetString()!;
        Assert.Equal(403, (await app.SendAsync("/downloads/42?format=pdf&title=Monthly%20report")).StatusCode);
        Assert.Equal(403, (await app.SendAsync(download.Replace("/42?", "/43?", StringComparison.Ordinal))).StatusCode);
        Assert.Equal(403, (await app.SendAsync(download.Replace("format=pdf", "format=csv", StringComparison.Ordinal))).StatusCode);
        Assert.Equal(403, (await app.SendAsync(download + "&extra=1")).StatusCode);

        var inspection = links.GetProperty("inspection").GetString()!;
        foreach (var (url, failure) in new[] { ("/inspect/42", "MissingSignature"), (inspection.Replace("/42?", "/43?", StringComparison.Ordinal), "UrlMismatch"), (inspection + "&signature=duplicate", "Malformed") })
        {
            var response = await app.SendAsync(url);
            Assert.Equal(403, response.StatusCode);
            var result = Json(response);
            Assert.False(result.GetProperty("isValid").GetBoolean());
            Assert.Equal(failure, result.GetProperty("failure").GetString());
            Assert.Equal(JsonValueKind.Null, result.GetProperty("data").ValueKind);
        }
    }

    [Fact]
    public async Task ShortLivedLinksExpireWhileThePermanentLinkKeepsWorking()
    {
        await using var app   = new SampleApplication("SignedUrls", "--SignedUrls:Key", Key);
        var             links = Json(await app.SendAsync("/links?seconds=1"));
        await Task.Delay(TimeSpan.FromSeconds(1.1));

        Assert.Equal(200, (await app.SendAsync(links.GetProperty("permanent").GetString()!)).StatusCode);
        Assert.Equal(403, (await app.SendAsync(links.GetProperty("temporary").GetString()!)).StatusCode);
        Assert.Equal(403, (await app.SendAsync(links.GetProperty("typed").GetString()!)).StatusCode);
        var inspection = await app.SendAsync(links.GetProperty("inspection").GetString()!);
        Assert.Equal(403, inspection.StatusCode);
        Assert.Equal("Expired", Json(inspection).GetProperty("failure").GetString());
        Assert.Equal(JsonValueKind.Null, Json(inspection).GetProperty("data").ValueKind);
    }

    [Fact]
    public async Task ConfiguredKeyKeepsLinksValidAcrossHostRestarts()
    {
        string url;
        await using (var first = new SampleApplication("SignedUrls", "--SignedUrls:Key", Key))
        {
            url = Json(await first.SendAsync("/links")).GetProperty("permanent").GetString()!;
        }

        await using var restarted = new SampleApplication("SignedUrls", "--SignedUrls:Key", Key);
        Assert.Equal(200, (await restarted.SendAsync(url)).StatusCode);

        var             differentKey = Convert.ToBase64String(Enumerable.Repeat((byte)99, 32).ToArray());
        await using var changed      = new SampleApplication("SignedUrls", "--SignedUrls:Key", differentKey);
        Assert.Equal(403, (await changed.SendAsync(url)).StatusCode);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("3601")]
    [InlineData("invalid")]
    public async Task InvalidLifetimesReturnBadRequests(string seconds)
    {
        await using var app = new SampleApplication("SignedUrls", "--SignedUrls:Key", Key);
        Assert.Equal(400, (await app.SendAsync("/links?seconds=" + seconds)).StatusCode);
    }

    private static JsonElement Json(MemoryResponse response)
    {
        using var document = JsonDocument.Parse(response.Text);
        return document.RootElement.Clone();
    }
}
