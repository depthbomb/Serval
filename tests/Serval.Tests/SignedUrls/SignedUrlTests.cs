#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.Extensions.DependencyInjection;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Serval.Binding;
using Serval.Hosting;
using Serval.Http;
using Serval.Http.Constants;
using Serval.Http.Results;
using Serval.Routing;
using Serval.SignedUrls;
using Serval.Tests.Support;

namespace Serval.Tests.SignedUrls;

public sealed class SignedUrlTests
{
    private static readonly byte[] Key = [.. Enumerable.Range(0, 32).Select(value => (byte)value)];

    [Fact]
    public async Task NamedRoutesIncludeReadableAuthenticatedRouteAndQueryValues()
    {
        await using var app    = CreateApplication();
        var             signer = app.Services.GetRequiredService<ISignedUrlService>();
        var url = signer.Path("signed.download", new Dictionary<string, object?>
        {
            ["id"] = 42
        }, new Dictionary<string, object?>
        {
            ["file"]   = "hello & 狸.txt",
            ["unused"] = null
        });
        Assert.StartsWith("/signed/download/42?file=hello%20%26%20%E7%8B%B8.txt&signature=", url);

        var payload = ReadPayload(url);
        Assert.Equal("signed.download", payload["routeName"]!.GetValue<string>());
        Assert.Equal("42", payload["routeValues"]!["id"]!.GetValue<string>());
        Assert.Equal("hello & 狸.txt", payload["query"]!["file"]![0]!.GetValue<string>());
        Assert.Null(payload["expiresAt"]);

        var result = signer.Validate(url);
        Assert.True(result.IsValid);
        Assert.Equal(SignedUrlFailure.None, result.Failure);
        Assert.Equal("/signed/download/42", result.Data!.Path);
        Assert.Equal("signed.download", result.Data.RouteName);
        Assert.Equal("42", result.Data.RouteValues["id"]);
        Assert.Equal("hello & 狸.txt", Assert.Single(result.Data.Query["FILE"]));
        Assert.Null(result.Data.ExpiresAt);
        Assert.Same(signer, app.Services.GetRequiredService<SignedUrlService>());
    }

    [Fact]
    public async Task RoutesKeepConstraintsDefaultsCatchAllsAndInvariantFormatting()
    {
        await using var app        = CreateApplication();
        var             signer     = app.Services.GetRequiredService<ISignedUrlService>();
        var             oldCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var url = signer.Path("signed.files", new Dictionary<string, object?>
            {
                ["path"] = "a b/狸"
            }, new Dictionary<string, object?>
            {
                ["price"] = 1.25m
            });
            Assert.StartsWith("/signed/files/a%20b/%E7%8B%B8?price=1.25&signature=", url);
            Assert.True(signer.IsValid(url));
            Assert.StartsWith("/signed/pages/2?signature=", signer.Path("signed.pages"));
            Assert.StartsWith("/signed/download/12?signature=", signer.Path("signed.download", new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["ID"] = 12
            }));
            Assert.Throws<ArgumentException>(() => signer.Path("signed.download"));
            Assert.Throws<ArgumentException>(() => signer.Path("signed.download", new Dictionary<string, object?>
            {
                ["id"] = 0
            }));
            Assert.Throws<ArgumentException>(() => signer.Path("missing"));
        }
        finally
        {
            CultureInfo.CurrentCulture = oldCulture;
        }
    }

    [Fact]
    public async Task ExistingGeneratedPathsCanBeSignedAndValidatedAsRequests()
    {
        await using var app    = CreateApplication();
        var             signer = app.Services.GetRequiredService<ISignedUrlService>();
        var path = app.Services.GetRequiredService<IUrlGenerator>().Path("signed.download", new Dictionary<string, object?>
        {
            ["id"] = 12
        });
        var             url     = signer.Sign(path);
        await using var request = app.Request("https://app.test" + url);
        Assert.True(signer.IsValid(request.Context.Request));
        Assert.True(request.Context.HasValidSignedUrl());
        Assert.Null(request.Context.RequireSignedUrl().RouteName);
        Assert.Empty(request.Context.RequireSignedUrl().RouteValues);
        Assert.True(signer.IsValid("http://another-origin.test" + url));

        await using var unsigned = app.Request("https://app.test" + path);
        Assert.False(unsigned.Context.HasValidSignedUrl());
        Assert.Equal(403, Assert.Throws<HttpErrorException>(() => unsigned.Context.RequireSignedUrl()).StatusCode);
    }

    [Fact]
    public async Task ExpirationRejectsAtTheExactInstantAndPermanentUrlsStayValid()
    {
        await using var app       = CreateApplication();
        var             signer    = app.Services.GetRequiredService<ISignedUrlService>();
        var             permanent = signer.Sign("/signed/pages/2");
        var             expiry    = app.Clock.Now.AddMinutes(5).AddTicks(1234);
        var             temporary = signer.Sign("/signed/pages/2", expiry.ToOffset(TimeSpan.FromHours(3)));
        var             lifetime  = signer.TemporaryPath("signed.pages", TimeSpan.FromMinutes(5));
        Assert.Equal(expiry, signer.Validate(temporary).Data!.ExpiresAt);
        Assert.Equal(app.Clock.Now.AddMinutes(5), signer.Validate(lifetime).Data!.ExpiresAt);
        Assert.Equal(TimeSpan.Zero, signer.Validate(temporary).Data!.ExpiresAt!.Value.Offset);
        app.Clock.Now = expiry.AddTicks(-1);
        Assert.True(signer.IsValid(temporary));
        app.Clock.Now = expiry;
        var expired = signer.Validate(temporary);
        Assert.Equal(SignedUrlFailure.Expired, expired.Failure);
        Assert.Null(expired.Data);
        app.Clock.Now = expiry.AddYears(20);
        Assert.False(signer.IsValid(temporary));
        Assert.True(signer.IsValid(permanent));
        Assert.Throws<ArgumentOutOfRangeException>(() => signer.Sign("/", app.Clock.Now));
        Assert.Throws<ArgumentOutOfRangeException>(() => signer.Sign("/", app.Clock.Now.AddTicks(-1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => signer.TemporaryPath("signed.pages", TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => signer.TemporaryPath("signed.pages", TimeSpan.FromSeconds(-1)));
    }

    [Theory]
    [InlineData("/signed/download/13?file=one&tag=a&tag=b")]
    [InlineData("/signed/download/12/?file=one&tag=a&tag=b")]
    [InlineData("/signed/download/12?file=two&tag=a&tag=b")]
    [InlineData("/signed/download/12?tag=a&tag=b")]
    [InlineData("/signed/download/12?file=one&tag=a&tag=b&extra=1")]
    [InlineData("/signed/download/12?file=one&tag=b&tag=a")]
    [InlineData("/signed/download/12?file=one&tag=a&tag=b&FILE=two")]
    public async Task ChangesToThePathOrAnyQueryValueAreRejected(string changed)
    {
        await using var app      = CreateApplication();
        var             signer   = app.Services.GetRequiredService<ISignedUrlService>();
        var             original = signer.Sign("/signed/download/12?file=one&tag=a&tag=b");
        var             result   = signer.Validate(changed + "&signature=" + Token(original));
        Assert.False(result.IsValid);
        Assert.Equal(SignedUrlFailure.UrlMismatch, result.Failure);
        Assert.Null(result.Data);
    }

    [Fact]
    public async Task QueryParsingMatchesTheFrameworkIncludingCaseAliasesAndRepeatedOrder()
    {
        await using var app        = CreateApplication();
        var             signer     = app.Services.GetRequiredService<ISignedUrlService>();
        var             original   = signer.Sign("/signed/pages/2?tag=a&TAG=b&q=a+b&flag&empty=");
        var             equivalent = "/signed/pages/2?empty=&flag=&q=a%20b&tag=a&tag=b&%73ignature=" + Token(original);
        Assert.True(signer.IsValid(equivalent));
        Assert.False(signer.IsValid(equivalent.Replace("tag=a&tag=b", "tag=b&tag=a", StringComparison.Ordinal)));
        await using var request = app.Request("https://app.test" + original);
        var             data    = request.Context.RequireSignedUrl();
        foreach (var pair in data.Query)
        {
            Assert.Equal(request.Context.Request.Query[pair.Key], pair.Value);
        }
    }

    [Theory]
    [InlineData("?x=1")]
    [InlineData("??x=1")]
    [InlineData("?%3Fx=1")]
    [InlineData("?x=1&&X=2&")]
    [InlineData("?=empty-name&flag&space=a+b&plus=a%2Bb")]
    [InlineData("?x=%26%3D%3F%23&%78=%2526")]
    public async Task QueryPayloadAndValidationAgreeWithRequestParsing(string query)
    {
        await using var app     = CreateApplication();
        var             signer  = app.Services.GetRequiredService<ISignedUrlService>();
        var             signed  = signer.Sign("/"                + query);
        await using var request = app.Request("https://app.test" + signed);
        var             result  = signer.Validate(request.Context.Request);
        Assert.True(result.IsValid);
        Assert.Equal(request.Context.Request.Query.Count - 1, result.Data!.Query.Count);
        foreach (var pair in result.Data.Query)
        {
            Assert.Equal(request.Context.Request.Query[pair.Key], pair.Value);
        }

        if (query == "?x=1")
        {
            Assert.False(signer.IsValid(signed.Replace("?x=", "??x=", StringComparison.Ordinal)));
        }
    }

    [Theory]
    [InlineData("signature")]
    [InlineData("SIGNATURE")]
    [InlineData("%73ignature")]
    public async Task DuplicateOrAlreadyPresentSignatureParametersAreRejected(string name)
    {
        await using var app    = CreateApplication();
        var             signer = app.Services.GetRequiredService<ISignedUrlService>();
        var             signed = signer.Sign("/");
        Assert.Equal(SignedUrlFailure.Malformed, signer.Validate(signed + "&" + name + "=" + Token(signed)).Failure);
        Assert.Throws<ArgumentException>(() => signer.Sign("/?" + name + "="));
        Assert.Throws<ArgumentException>(() => signer.Sign(signed));
    }

    [Fact]
    public async Task ChangingPayloadClaimsOrTheMacCannotForgeAValidUrl()
    {
        await using var app    = CreateApplication();
        var             signer = app.Services.GetRequiredService<ISignedUrlService>();
        var signed = signer.Path("signed.download", new Dictionary<string, object?>
        {
            ["id"] = 12
        }, expiresAt: app.Clock.Now.AddMinutes(1));
        var token = Token(signed);
        foreach (var claim in new[] { "routeName", "routeValues", "expiresAt", "path", "query", "version" })
        {
            var payload = ReadPayload(signed);
            payload[claim] = null;
            var forged = Encode(Encoding.UTF8.GetBytes(payload.ToJsonString())) + token[token.IndexOf('.')..];
            Assert.Equal(SignedUrlFailure.InvalidSignature, signer.Validate(signed.Replace(token, forged, StringComparison.Ordinal)).Failure);
        }

        var mac = Decode(token[(token.IndexOf('.') + 1)..]);
        mac[0] ^= 1;
        var badMac = token[..(token.IndexOf('.') + 1)] + Encode(mac);
        Assert.Equal(SignedUrlFailure.InvalidSignature, signer.Validate(signed.Replace(token, badMac, StringComparison.Ordinal)).Failure);
    }

    [Fact]
    public async Task SeparateInstancesShareOnlyTheConfiguredKeyAndSnapshotOptions()
    {
        SignedUrlOptions? configured = null;
        var               mutableKey = Key.ToArray();
        await using var first = CreateApplication(options =>
        {
            options.SigningKey = mutableKey;
            configured         = options;
        });
        mutableKey[0]                      ^= 1;
        configured!.SignatureParameterName =  "changed";
        configured.MaxUrlLength            =  256;
        await using var second = CreateApplication();
        await using var other  = CreateApplication(options => options.SigningKey = RandomNumberGenerator.GetBytes(32));
        var             signed = first.Services.GetRequiredService<ISignedUrlService>().Sign("/signed/pages/2");
        Assert.Contains("?signature=", signed);
        Assert.True(second.Services.GetRequiredService<ISignedUrlService>().IsValid(signed));
        Assert.Equal(SignedUrlFailure.InvalidSignature, other.Services.GetRequiredService<ISignedUrlService>().Validate(signed).Failure);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("//elsewhere.test/path")]
    [InlineData("relative/path")]
    [InlineData("ftp://app.test/path")]
    [InlineData("https://user:password@app.test/path")]
    [InlineData("/path#fragment")]
    [InlineData("/path\\other")]
    [InlineData("/path\n")]
    [InlineData("/path?x=%")]
    [InlineData("/path?signature=")]
    [InlineData("/path?signature=abc")]
    [InlineData("/path?signature=abc.def")]
    public async Task MalformedInputReturnsAFailureWithoutThrowing(string? url)
    {
        await using var app    = CreateApplication();
        var             signer = app.Services.GetRequiredService<ISignedUrlService>();
        Assert.Equal(SignedUrlFailure.Malformed, signer.Validate(url).Failure);
        Assert.Null(signer.Validate(url).Data);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("{broken")]
    public async Task EvenAuthenticatedMalformedJsonFailsCleanly(string payload)
    {
        await using var app     = CreateApplication();
        var             signer  = app.Services.GetRequiredService<ISignedUrlService>();
        var             url     = "/?signature=" + SignPayload(payload);
        await using var request = app.Request("https://app.test" + url);
        AssertMalformed(signer, url, request.Context.Request);
    }

    [Theory]
    [InlineData("version", "2")]
    [InlineData("path", "null")]
    [InlineData("query", "null")]
    [InlineData("query", "{\"x\":null}")]
    [InlineData("query", "{\"x\":[null]}")]
    [InlineData("query", "{\"x\":[\"valid\",null]}")]
    [InlineData("query", "{\"x\":[],\"X\":[]}")]
    [InlineData("routeValues", "null")]
    [InlineData("expiresAt", "\"not-a-date\"")]
    public async Task UnsupportedOrInvalidAuthenticatedClaimsFailCleanly(string claim, string value)
    {
        await using var app     = CreateApplication();
        var             signer  = app.Services.GetRequiredService<ISignedUrlService>();
        var             payload = ReadPayload(signer.Sign("/"));
        payload[claim] = JsonNode.Parse(value);
        var             url     = "/?signature=" + SignPayload(payload.ToJsonString());
        await using var request = app.Request("https://app.test" + url);
        AssertMalformed(signer, url, request.Context.Request);
    }

    [Theory]
    [InlineData("version")]
    [InlineData("path")]
    [InlineData("routeName")]
    [InlineData("routeValues")]
    [InlineData("query")]
    [InlineData("expiresAt")]
    public async Task RequiredClaimsMustBePresentEvenWhenTheirValuesMayBeNull(string claim)
    {
        await using var app     = CreateApplication();
        var             signer  = app.Services.GetRequiredService<ISignedUrlService>();
        var             payload = ReadPayload(signer.Sign("/"));
        payload.Remove(claim);
        var             url     = "/?signature=" + SignPayload(payload.ToJsonString());
        await using var request = app.Request("https://app.test" + url);
        AssertMalformed(signer, url, request.Context.Request);
    }

    [Fact]
    public async Task VerifiedDataPreservesSignedNamesAndAllowedNullMetadata()
    {
        await using var app     = CreateApplication();
        var             signer  = app.Services.GetRequiredService<ISignedUrlService>();
        var             payload = ReadPayload(signer.Sign("/?File=one&File=two"));
        payload["routeValues"]!["optional"] = null;
        var             url     = "/?FILE=one&FILE=two&signature=" + SignPayload(payload.ToJsonString());
        await using var request = app.Request("https://app.test" + url);
        foreach (var result in new[] { signer.Validate(url), signer.Validate(request.Context.Request) })
        {
            Assert.True(result.IsValid);
            var data = Assert.IsType<SignedUrlData>(result.Data);
            Assert.Null(data.RouteName);
            Assert.Null(data.ExpiresAt);
            Assert.Null(data.RouteValues["optional"]);
            Assert.Equal("File", Assert.Single(data.Query.Keys));
            Assert.Equal(new[] { "one", "two" }, data.Query["file"]);
        }

        Assert.True(signer.IsValid(url));
        Assert.True(signer.IsValid(request.Context.Request));
    }

    [Fact]
    public async Task UrlLimitsAndReservedNamesApplyDuringSigningAndValidation()
    {
        await using var app = CreateApplication(options =>
        {
            options.SignatureParameterName = "_signed";
            options.MaxUrlLength           = 1024;
        });
        var signer = app.Services.GetRequiredService<ISignedUrlService>();
        var url    = signer.Sign("/?signature=ordinary-data");
        Assert.Contains("&_signed=", url);
        Assert.True(signer.IsValid(url));
        Assert.Throws<ArgumentException>(() => signer.Sign("/?_SIGNED=collision"));
        Assert.Throws<ArgumentException>(() => signer.Sign("/" + new string('a', 900)));
        Assert.Equal(SignedUrlFailure.Malformed, signer.Validate("/" + new string('a', 1024)).Failure);
        Assert.Equal(SignedUrlFailure.MissingSignature, signer.Validate("/").Failure);
        Assert.Throws<ArgumentException>(() => signer.Sign("https://app.test/"));
        Assert.Throws<ArgumentException>(() => signer.Sign("//app.test/"));
    }

    [Fact]
    public void InvalidConfigurationFailsAtRegistration()
    {
        var builder = new ServiceCollection().AddServal();
        Assert.Throws<ArgumentException>(() => builder.AddSignedUrls(_ => {}));
        Assert.Throws<ArgumentException>(() => builder.AddSignedUrls(options => options.SigningKey = new byte[31]));
        Assert.Throws<ArgumentException>(() => builder.AddSignedUrls(options =>
        {
            options.SigningKey             = Key;
            options.SignatureParameterName = "bad&name";
        }));
        Assert.Throws<ArgumentOutOfRangeException>(() => builder.AddSignedUrls(options =>
        {
            options.SigningKey   = Key;
            options.MaxUrlLength = 255;
        }));
        Assert.Same(builder, builder.AddSignedUrls(options => options.SigningKey = Key));
        Assert.Throws<InvalidOperationException>(() => builder.AddSignedUrls(options => options.SigningKey = Key));
    }

    [Fact]
    public async Task GeneratedControllerInjectionAndMiddlewareEnforceSignedUrls()
    {
        var clock = new TestTimeProvider();
        await using var app = new TestApplication(
            configureServices: services => services.AddSingleton<TimeProvider>(clock),
            configureBuilder: builder => builder.AddSignedUrls(options => options.SigningKey = Key));
        var signer = app.Services.GetRequiredService<ISignedUrlService>();
        var url = signer.Path("signed.generated", new Dictionary<string, object?>
        {
            ["id"] = 7
        }, expiresAt: clock.Now.AddMinutes(1));
        var valid = await app.SendAsync(url);
        Assert.Equal(200, valid.StatusCode);
        Assert.Equal("7:True", valid.Text);
        Assert.Equal(403, (await app.SendAsync("/signed/generated/7")).StatusCode);
        Assert.Equal(403, (await app.SendAsync(url.Replace("/7?", "/8?", StringComparison.Ordinal))).StatusCode);
        clock.Now += TimeSpan.FromMinutes(1);
        Assert.Equal(403, (await app.SendAsync(url)).StatusCode);
    }

    [Fact]
    public async Task ConcurrentRequestsDoNotShareMutableSigningState()
    {
        await using var app    = CreateApplication();
        var             signer = app.Services.GetRequiredService<ISignedUrlService>();
        await Parallel.ForEachAsync(Enumerable.Range(1, 100), (id, _) =>
        {
            var url = signer.Path("signed.download", new Dictionary<string, object?>
            {
                ["id"] = id
            });
            var result = signer.Validate(url);
            Assert.True(result.IsValid);
            Assert.Equal(id.ToString(CultureInfo.InvariantCulture), result.Data!.RouteValues["id"]);
            return ValueTask.CompletedTask;
        });
    }

    [Theory]
    [InlineData("/a/..//other.test/report")]
    [InlineData("/%2e%2e//other.test/report")]
    [InlineData("/a/%2e%2e//other.test/report?format=pdf")]
    public async Task SigningNeverReturnsANetworkPathAfterNormalization(string path)
    {
        await using var app    = CreateApplication();
        var             signer = app.Services.GetRequiredService<ISignedUrlService>();
        Assert.Throws<ArgumentException>(() => signer.Sign(path));
    }

    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("a/../../pages/2")]
    public async Task NamedRouteValuesCannotNormalizeIntoAnotherRoute(string path)
    {
        await using var app    = CreateApplication();
        var             signer = app.Services.GetRequiredService<ISignedUrlService>();
        Assert.Throws<ArgumentException>(() => signer.Path("signed.files", new Dictionary<string, object?>
        {
            ["path"] = path
        }));
        Assert.True(signer.IsValid(signer.Path("signed.files", new Dictionary<string, object?>
        {
            ["path"] = "%2e%2e/file.txt"
        })));
        Assert.True(signer.IsValid(signer.Sign("/signed/files/a/../file.txt")));
    }

    [Fact]
    public async Task UrlLengthLimitIsConsistentForRelativeAbsoluteAndRequestUrls()
    {
        await using var app      = CreateApplication();
        var             urls     = app.Services.GetRequiredService<IUrlGenerator>();
        var             original = app.Services.GetRequiredService<ISignedUrlService>().Sign("/signed/pages/2?title=" + new string('a', 64));
        var signer = new SignedUrlService(urls, new SignedUrlOptions
        {
            SigningKey   = Key,
            MaxUrlLength = original.Length
        });
        Assert.Equal(original, signer.Sign("/signed/pages/2?title=" + new string('a', 64)));
        Assert.True(signer.IsValid(original));
        Assert.True(signer.IsValid("https://app.test:8443"            + original));
        await using var request = app.Request("https://app.test:8443" + original);
        Assert.True(signer.IsValid(request.Context.Request));
        Assert.True(signer.Validate(request.Context.Request).IsValid);
        Assert.Equal(SignedUrlFailure.Malformed, signer.Validate(original           + "&").Failure);
        Assert.Equal(SignedUrlFailure.Malformed, signer.Validate("https://app.test" + original + "&").Failure);
    }

    [Fact]
    public async Task AuthenticatedJsonRejectsDuplicatePropertiesAtEveryLevel()
    {
        await using var app        = CreateApplication();
        var             signer     = app.Services.GetRequiredService<ISignedUrlService>();
        var             json       = ReadPayload(signer.Sign("/?x=1")).ToJsonString();
        var             duplicates = new[] { json.Replace("\"version\":1", "\"version\":1,\"version\":1", StringComparison.Ordinal), json.Replace("\"expiresAt\":null", "\"expiresAt\":\"2020-01-01T00:00:00Z\",\"expiresAt\":null", StringComparison.Ordinal), json.Replace("\"x\":[\"1\"]", "\"x\":[\"0\"],\"x\":[\"1\"]", StringComparison.Ordinal), json.Replace("\"routeValues\":{}", "\"routeValues\":{\"id\":\"1\",\"id\":\"2\"}", StringComparison.Ordinal), };
        foreach (var duplicate in duplicates)
        {
            Assert.NotEqual(json, duplicate);
            var result = signer.Validate("/?x=1&signature=" + SignPayload(duplicate));
            Assert.Equal(SignedUrlFailure.Malformed, result.Failure);
            Assert.Null(result.Data);
        }
    }

    [Fact]
    public async Task ValidationUsesTheRequestPathWithoutRecanonicalizingIt()
    {
        await using var app    = CreateApplication();
        var             signer = app.Services.GetRequiredService<ISignedUrlService>();
        var             signed = signer.Sign("/signed/pages/2");
        var raw = new Uri("https://app.test/other/.." + signed, new UriCreationOptions
        {
            DangerousDisablePathAndQueryCanonicalization = true
        });
        var request = new Request("GET", raw, HttpVersion.Version11, new Headers(), Stream.Null, 0);
        Assert.StartsWith("/other/..", request.Url.AbsolutePath);
        Assert.False(signer.IsValid(request));
        Assert.False(signer.Validate(request).IsValid);
    }

    [Fact]
    public async Task RepeatedChecksRespectExpirationAndReturnIndependentImmutableData()
    {
        await using var app    = CreateApplication();
        var             signer = app.Services.GetRequiredService<ISignedUrlService>();
        var signed = signer.Path("signed.download", new Dictionary<string, object?>
        {
            ["id"] = 12
        }, new Dictionary<string, object?>
        {
            ["x"] = "one"
        }, app.Clock.Now.AddMinutes(1));
        await using var request = app.Request("https://app.test" + signed);
        var             data    = signer.Validate(request.Context.Request).Data!;
        if (data.Query is IDictionary<string, IReadOnlyList<string>> query)
        {
            Assert.Throws<NotSupportedException>(() => query["extra"] = ["two"]);
        }

        if (data.RouteValues is IDictionary<string, string?> routeValues)
        {
            Assert.Throws<NotSupportedException>(() => routeValues["id"] = "13");
        }

        Assert.Throws<NotSupportedException>(() => ((IList<string>)data.Query["x"])[0] = "changed");
        // Request.Query is mutable through its arrays; it must never replace the URL as signing input.
        request.Context.Request.Query["x"][0] = "changed";
        Assert.Equal("one", signer.Validate(request.Context.Request).Data!.Query["x"][0]);
        Assert.True(request.Context.HasValidSignedUrl());
        app.Clock.Now += TimeSpan.FromMinutes(1);
        Assert.False(request.Context.HasValidSignedUrl());
        Assert.False(signer.IsValid(request.Context.Request));
        Assert.Equal(SignedUrlFailure.Expired, signer.Validate(request.Context.Request).Failure);
    }

    [Fact]
    public async Task RouteObjectsAreFormattedOnceWithoutLosingCaseInsensitiveLookup()
    {
        await using var app    = CreateApplication();
        var             signer = app.Services.GetRequiredService<ISignedUrlService>();
        var             value  = new ChangingRouteValue();
        var signed = signer.Path("signed.download", new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["ID"] = value
        });
        Assert.StartsWith("/signed/download/12?", signed);
        Assert.Equal("12", signer.Validate(signed).Data!.RouteValues["ID"]);
        Assert.Equal(1, value.Calls);
    }

    [Fact]
    public async Task EquivalentQueryEncodingsRoundTripAcrossBothValidationOverloads()
    {
        await using var app    = CreateApplication();
        var             signer = app.Services.GetRequiredService<ISignedUrlService>();
        var             random = new Random(20261113);
        string[]        names  = ["x", "X", "tag", "Tag", "?name", "", "é", "İ", "ı", "ß"];
        string[]        values = ["", "a b", "a+b", "a&b", "x=y", "?", "#", "%2F", "é", "狸", "\0", "\r\n", "\\"];
        for (var index = 0; index < 250; index++)
        {
            var query = string.Join('&', Enumerable.Range(0, random.Next(1, 20)).Select(_ =>
                Uri.EscapeDataString(names[random.Next(names.Length)]) + "=" + Uri.EscapeDataString(values[random.Next(values.Length)])));
            var             signed   = signer.Sign("/signed/pages/2?" + query);
            var             absolute = "https://app.test" + signed;
            await using var request  = app.Request(absolute);
            Assert.True(signer.IsValid(signed));
            Assert.True(signer.IsValid(absolute));
            Assert.True(signer.IsValid(request.Context.Request));
            var data = signer.Validate(request.Context.Request).Data!;
            foreach (var pair in data.Query)
            {
                Assert.Equal(request.Context.Request.Query[pair.Key], pair.Value);
            }

            Assert.False(signer.IsValid(signed + "&unsigned=1"));
        }
    }

    [Fact]
    public async Task MalformedBase64AndNonzeroPaddingBitsAreRejectedWithoutExceptions()
    {
        await using var app                   = CreateApplication();
        var             signer                = app.Services.GetRequiredService<ISignedUrlService>();
        var             token                 = Token(signer.Sign("/"));
        var             separator             = token.IndexOf('.');
        var             encoded               = token[..separator];
        var             signature             = token[(separator + 1)..];
        const string    alphabet              = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
        var             noncanonicalSignature = signature[..^1] + alphabet[alphabet.IndexOf(signature[^1]) | 1];
        var             malformed             = new[] { encoded + "." + noncanonicalSignature, encoded + "=." + signature, "A." + signature, "AB." + signature, "AAB." + signature, "a+a." + signature, "a/a." + signature, "a%20a." + signature, "a%0Aa." + signature, "é." + signature, encoded + ".." + signature, };
        foreach (var invalid in malformed)
        {
            var url = "/?signature=" + invalid;
            Assert.Equal(SignedUrlFailure.Malformed, signer.Validate(url).Failure);
            Assert.False(signer.IsValid(url));
        }
    }

    private static void AssertMalformed(ISignedUrlService signer, string url, Request request)
    {
        foreach (var result in new[] { signer.Validate(url), signer.Validate(request) })
        {
            Assert.Equal(SignedUrlFailure.Malformed, result.Failure);
            Assert.False(result.IsValid);
            Assert.Null(result.Data);
        }

        Assert.False(signer.IsValid(url));
        Assert.False(signer.IsValid(request));
    }

    private static SecurityTestApplication CreateApplication(Action<SignedUrlOptions>? configure = null)
        => new(builder =>
        {
            builder.AddSignedUrls(options =>
            {
                options.SigningKey = Key;
                configure?.Invoke(options);
            });
            builder.AddController<object>(HttpMethods.Get, "/signed/download/{id:int:min(1)}", "signed.download", (_, _) => ValueTask.FromResult<IActionResult>(new TextResult("download")));
            builder.AddController<object>(HttpMethods.Get, "/signed/pages/{page:int=2}", "signed.pages", (_,          _) => ValueTask.FromResult<IActionResult>(new TextResult("page")));
            builder.AddController<object>(HttpMethods.Get, "/signed/files/{**path}", "signed.files", (_,              _) => ValueTask.FromResult<IActionResult>(new TextResult("file")));
        });

    private static string Token(string url) => url[(url.IndexOf("signature=", StringComparison.Ordinal) + "signature=".Length)..];

    private static JsonObject ReadPayload(string url)
        => JsonNode.Parse(Decode(Token(url).Split('.')[0]))!.AsObject();

    private static string SignPayload(string json)
    {
        var encoded = Encode(Encoding.UTF8.GetBytes(json));
        return encoded + "." + Encode(HMACSHA256.HashData(Key, Encoding.ASCII.GetBytes("Serval.SignedUrls.v1." + encoded)));
    }

    private static string Encode(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Decode(string value) => Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/').PadRight((value.Length + 3) / 4 * 4, '='));

    private sealed class ChangingRouteValue : IFormattable
    {
        public int Calls { get; private set; }

        public string ToString(string? format, IFormatProvider? formatProvider) => (11 + ++Calls).ToString(formatProvider);
    }
}

[Controller]
public sealed class SignedUrlTestController : ControllerBase
{
    [Get("/signed/generated/{id:int}", Name = "signed.generated")]
    [RequireSignedUrl]
    public ValueTask<IActionResult> DownloadAsync([FromServices] ISignedUrlService signer) => TextAsync($"{RouteValue<int>("id")}:{signer.IsValid(Request)}");
}
