using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Serval.Authentication;
using Serval.Hosting;
using Serval.Http.Constants;
using Serval.OAuth;
using Serval.Tests.Support;

namespace Serval.Tests.OAuth;

public sealed class OpenIdConnectTests
{
    [Fact]
    public async Task DiscoveryNoncePkceAndValidatedIdentityCreateASessionAndRejectReplay()
    {
        using var provider = new OidcTestProvider();
        using var http = new HttpClient(provider);
        OpenIdConnectIdentity? mapped = null;
        await using var app = Application(http, options =>
        {
            options.OpenIdConnect!.GetClaimsFromUserInfoEndpoint = true;
            options.CreatePrincipal = context =>
            {
                mapped = context.OpenIdConnect!;
                return ValueTask.FromResult(mapped.Principal);
            };
        });
        var (callback, cookie, query) = await Begin(app, provider);
        Assert.Equal("openid profile", query["scope"].Single());
        Assert.Equal("S256", query["code_challenge_method"].Single());
        Assert.Equal(43, provider.Nonce.Length);
        Assert.NotEqual(query["state"].Single(), provider.Nonce);
        await using var complete = app.Request(callback, cookie);
        var result = await complete.Context.CompleteOAuthSignInAsync("provider");
        Assert.Equal("/account", result.ReturnUrl);
        Assert.Equal("Ada", result.Principal.Identity!.Name);
        Assert.Equal("subject", result.Principal.FindFirst("sub")!.Value);
        Assert.Equal("subject", mapped!.Claims.GetProperty("sub").GetString());
        Assert.Equal("UserInfo name", mapped.UserInfo!.Value.GetProperty("name").GetString());
        Assert.Equal(query["code_challenge"].Single(), OAuthClientTests.Challenge(OAuthClientTests.Form(provider.TokenBody!)["code_verifier"].Single()));
        var session = SecurityTestApplication.Cookie(complete.Response, "__Host-Serval.Session=");
        await using var authenticated = app.Request(cookie: session);
        Assert.True((await authenticated.Context.AuthenticateAsync()).Succeeded);
        Assert.DoesNotContain(result.Tokens.IdToken!, string.Join(';', complete.Response.Headers.GetValues(HeaderNames.SetCookie)));
        await using var replay = app.Request(callback, cookie);
        Assert.Equal("invalid_state", (await Assert.ThrowsAsync<OAuthProtocolException>(async () => await replay.Context.CompleteOAuthSignInAsync("provider"))).Error);
        Assert.Equal(1, provider.TokenCalls);
    }

    [Theory]
    [InlineData("RS256")]
    [InlineData("RS384")]
    [InlineData("RS512")]
    [InlineData("PS256")]
    [InlineData("PS384")]
    [InlineData("PS512")]
    [InlineData("ES256")]
    [InlineData("ES384")]
    [InlineData("ES512")]
    public async Task ExplicitAsymmetricAlgorithmsAreVerified(string algorithm)
    {
        using var provider = new OidcTestProvider { Algorithm = algorithm };
        if (algorithm.StartsWith("ES", StringComparison.Ordinal))
        {
            provider.EcKey = ECDsa.Create(algorithm switch { "ES256" => ECCurve.NamedCurves.nistP256, "ES384" => ECCurve.NamedCurves.nistP384, _ => ECCurve.NamedCurves.nistP521 });
        }
        using var http = new HttpClient(provider);
        await using var app = Application(http, options =>
        {
            options.OpenIdConnect!.SigningAlgorithms.Clear();
            options.OpenIdConnect.SigningAlgorithms.Add(algorithm);
        });
        var (callback, cookie, _) = await Begin(app, provider);
        await using var complete = app.Request(callback, cookie);
        Assert.True((await complete.Context.CompleteOAuthSignInAsync("provider")).Principal.Identity!.IsAuthenticated);
    }

    [Theory]
    [InlineData("issuer")]
    [InlineData("issuer-slash")]
    [InlineData("audience")]
    [InlineData("audience-slash")]
    [InlineData("extra-audience")]
    [InlineData("azp")]
    [InlineData("nonce")]
    [InlineData("missing-nonce")]
    [InlineData("expired")]
    [InlineData("future-iat")]
    [InlineData("missing-iat")]
    [InlineData("string-exp")]
    [InlineData("missing-exp")]
    [InlineData("huge-exp")]
    [InlineData("future-nbf")]
    [InlineData("empty-sub")]
    [InlineData("numeric-sub")]
    [InlineData("none")]
    [InlineData("hmac")]
    [InlineData("critical-header")]
    [InlineData("bad-signature")]
    [InlineData("duplicate-claim")]
    [InlineData("malformed")]
    [InlineData("encrypted")]
    [InlineData("oversized")]
    [InlineData("at-hash")]
    [InlineData("c-hash")]
    [InlineData("userinfo-sub")]
    [InlineData("old-auth-time")]
    [InlineData("missing-auth-time")]
    public async Task InvalidIdentityNeverReachesMappingOrCreatesASession(string failure)
    {
        using var provider = new OidcTestProvider();
        using var http = new HttpClient(provider);
        var mapped = false;
        await using var app = Application(http, options =>
        {
            options.OpenIdConnect!.GetClaimsFromUserInfoEndpoint = true;
            if (failure.Contains("auth-time", StringComparison.Ordinal))
            {
                options.OpenIdConnect.MaxAge = TimeSpan.FromMinutes(5);
            }
            options.CreatePrincipal = context =>
            {
                mapped = true;
                return ValueTask.FromResult(context.OpenIdConnect!.Principal);
            };
        });
        var (callback, cookie, _) = await Begin(app, provider);
        provider.ChangeClaims = claims =>
        {
            switch (failure)
            {
                case "issuer": claims["iss"] = "https://attacker.test"; break;
                case "issuer-slash": claims["iss"] = "https://provider.test/tenant/"; break;
                case "audience": claims["aud"] = "other-client"; break;
                case "audience-slash": claims["aud"] = "client/"; break;
                case "extra-audience": claims["aud"] = new[] { "client", "untrusted" }; claims["azp"] = "client"; break;
                case "azp": claims["azp"] = "other-client"; break;
                case "nonce": claims["nonce"] = "other-attempt"; break;
                case "missing-nonce": claims.Remove("nonce"); break;
                case "expired": claims["exp"] = provider.Now.AddMinutes(-2).ToUnixTimeSeconds(); break;
                case "future-iat": claims["iat"] = provider.Now.AddMinutes(2).ToUnixTimeSeconds(); break;
                case "missing-iat": claims.Remove("iat"); break;
                case "missing-exp": claims.Remove("exp"); break;
                case "string-exp": claims["exp"] = "9999999999"; break;
                case "huge-exp": claims["exp"] = long.MaxValue; break;
                case "future-nbf": claims["nbf"] = provider.Now.AddMinutes(2).ToUnixTimeSeconds(); break;
                case "empty-sub": claims["sub"] = ""; break;
                case "numeric-sub": claims["sub"] = 123; break;
                case "at-hash": claims["at_hash"] = "bad"; break;
                case "c-hash": claims["c_hash"] = "bad"; break;
                case "old-auth-time": claims["auth_time"] = provider.Now.AddHours(-1).ToUnixTimeSeconds(); break;
            }
        };
        provider.ChangeHeader = header =>
        {
            if (failure == "none") { header["alg"] = "none"; }
            if (failure == "hmac") { header["alg"] = "HS256"; }
            if (failure == "critical-header") { header["crit"] = new[] { "unknown" }; }
        };
        provider.ChangeToken = token => failure switch
        {
            "bad-signature" => token[..(token.LastIndexOf('.') + 1)] + OidcTestProvider.Encode(new byte[256]),
            "duplicate-claim" => token.Split('.')[0] + "." + OidcTestProvider.Encode(Encoding.UTF8.GetBytes("{\"sub\":\"one\",\"sub\":\"two\"}")) + ".sig",
            "malformed" => "not-a-jwt",
            "encrypted" => "a.b.c.d.e",
            "oversized" => new string('a', 65537),
            _ => token,
        };
        if (failure == "userinfo-sub") { provider.UserSubject = "attacker"; }
        await using var complete = app.Request(callback, cookie);
        var error = await Assert.ThrowsAsync<OAuthProtocolException>(async () => await complete.Context.CompleteOAuthSignInAsync("provider"));
        Assert.Null(error.InnerException);
        Assert.False(mapped);
        Assert.DoesNotContain(complete.Response.Headers.GetValues(HeaderNames.SetCookie), value => value.StartsWith("__Host-Serval.Session=", StringComparison.Ordinal));
        Assert.Contains(complete.Response.Headers.GetValues(HeaderNames.SetCookie), value => value.Contains("Max-Age=0", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RotatedSigningKeysRefreshOnceIncludingReusedKeyIds(bool sameId)
    {
        using var provider = new OidcTestProvider();
        using var http = new HttpClient(provider);
        await using var app = Application(http);
        var (callback, cookie, _) = await Begin(app, provider);
        provider.Rotate(sameId);
        await using var complete = app.Request(callback, cookie);
        await complete.Context.CompleteOAuthSignInAsync("provider");
        Assert.Equal(2, provider.KeyCalls);
        var (again, anotherCookie, _) = await Begin(app, provider);
        await using var next = app.Request(again, anotherCookie);
        await next.Context.CompleteOAuthSignInAsync("provider");
        Assert.Equal(2, provider.KeyCalls);
    }

    [Fact]
    public async Task UnknownKeysCannotCauseUnboundedRefreshesAndRecoverAfterCooldown()
    {
        using var provider = new OidcTestProvider();
        using var http = new HttpClient(provider);
        await using var app = Application(http);
        await Begin(app, provider);
        provider.ChangeHeader = header => header["kid"] = "unknown";
        for (var i = 0; i < 3; i++)
        {
            var (callback, cookie, _) = await Begin(app, provider);
            await using var request = app.Request(callback, cookie);
            await Assert.ThrowsAsync<OAuthProtocolException>(async () => await request.Context.CompleteOAuthSignInAsync("provider"));
        }
        Assert.Equal(2, provider.KeyCalls);
        provider.ChangeHeader = null;
        provider.Rotate();
        app.Clock.Now += TimeSpan.FromSeconds(31);
        var (valid, validCookie, _) = await Begin(app, provider);
        await using var complete = app.Request(valid, validCookie);
        await complete.Context.CompleteOAuthSignInAsync("provider");
        Assert.Equal(3, provider.KeyCalls);
    }

    [Theory]
    [InlineData("issuer")]
    [InlineData("http-keys")]
    [InlineData("missing-token")]
    [InlineData("implicit-only")]
    [InlineData("no-supported-algorithm")]
    [InlineData("plain-pkce")]
    [InlineData("endpoint-nonce")]
    [InlineData("endpoint-max-age")]
    public async Task InvalidDiscoveryIsRejectedBeforeRedirectOrCredentials(string failure)
    {
        using var provider = new OidcTestProvider();
        provider.ChangeMetadata = metadata =>
        {
            switch (failure)
            {
                case "issuer": metadata["issuer"] = "https://attacker.test"; break;
                case "http-keys": metadata["jwks_uri"] = "http://provider.test/keys"; break;
                case "missing-token": metadata.Remove("token_endpoint"); break;
                case "implicit-only": metadata["response_types_supported"] = new[] { "id_token" }; break;
                case "no-supported-algorithm": metadata["id_token_signing_alg_values_supported"] = new[] { "none" }; break;
                case "plain-pkce": metadata["code_challenge_methods_supported"] = new[] { "plain" }; break;
                case "endpoint-nonce": metadata["authorization_endpoint"] = "https://provider.test/authorize?nonce=override"; break;
                case "endpoint-max-age": metadata["authorization_endpoint"] = "https://provider.test/authorize?max_age=0"; break;
            }
        };
        using var http = new HttpClient(provider);
        await using var app = Application(http);
        await using var request = app.Request("https://app.test/login");
        await Assert.ThrowsAsync<OAuthProtocolException>(async () => await request.Context.BeginOAuthSignInAsync("provider"));
        Assert.Empty(request.Response.Headers.GetValues(HeaderNames.SetCookie));
        Assert.Equal(0, provider.TokenCalls);
    }

    [Theory]
    [InlineData("redirect")]
    [InlineData("oversize")]
    public async Task DiscoveryRetainsBackchannelRedirectAndSizeLimits(string failure)
    {
        using var provider = new OidcTestProvider();
        provider.Respond = _ => failure == "redirect"
            ? new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://attacker.test") } }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new string('x', 2048)) };
        using var http = new HttpClient(provider);
        await using var app = Application(http, options => options.MaximumResponseBytes = 1024);
        await using var request = app.Request("https://app.test/login");
        var error = await Assert.ThrowsAsync<OAuthProtocolException>(async () => await request.Context.BeginOAuthSignInAsync("provider"));
        Assert.Equal(failure == "redirect" ? "unexpected_redirect" : "response_too_large", error.Error);
    }

    [Fact]
    public async Task DiscoveryIsSharedAcrossConcurrentRequestsAndExpires()
    {
        using var provider = new OidcTestProvider();
        using var http = new HttpClient(provider);
        await using var app = Application(http);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            await using var request = app.Request("https://app.test/login");
            await request.Context.BeginOAuthSignInAsync("provider");
        }));
        Assert.Equal(1, provider.DiscoveryCalls);
        Assert.Equal(1, provider.KeyCalls);
        app.Clock.Now += TimeSpan.FromHours(13);
        await Begin(app, provider);
        Assert.Equal(2, provider.DiscoveryCalls);
    }

    [Fact]
    public async Task MaxAgeTrustedAudiencesAndTokenHashesAreChecked()
    {
        using var provider = new OidcTestProvider();
        provider.ChangeClaims = claims =>
        {
            claims["aud"] = new[] { "client", "api" };
            claims["azp"] = "client";
            claims["auth_time"] = provider.Now.ToUnixTimeSeconds();
            claims["at_hash"] = OidcTestProvider.Encode(SHA256.HashData(Encoding.ASCII.GetBytes("access-secret"))[..16]);
            claims["c_hash"] = OidcTestProvider.Encode(SHA256.HashData(Encoding.ASCII.GetBytes("code"))[..16]);
        };
        using var http = new HttpClient(provider);
        await using var app = Application(http, options =>
        {
            options.OpenIdConnect!.MaxAge = TimeSpan.FromMinutes(5);
            options.OpenIdConnect.AdditionalAudiences.Add("api");
        });
        var (callback, cookie, query) = await Begin(app, provider);
        Assert.Equal("300", query["max_age"].Single());
        await using var complete = app.Request(callback, cookie);
        await complete.Context.CompleteOAuthSignInAsync("provider");
    }

    [Theory]
    [InlineData("nonce")]
    [InlineData("max_age")]
    [InlineData("request")]
    [InlineData("request_uri")]
    public async Task AdditionalParametersCannotOverrideManagedOidcSecurity(string name)
    {
        using var provider = new OidcTestProvider();
        using var http = new HttpClient(provider);
        await using var app = Application(http);
        await using var request = app.Request("https://app.test/login");
        await Assert.ThrowsAsync<ArgumentException>(async () => await request.Context.BeginOAuthSignInAsync("provider", parameters: new Dictionary<string, string> { [name] = "override" }));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("https://wrong.test", false)]
    public async Task CallbackIssuerIsCheckedBeforeCodeExchange(string? issuer, bool required)
    {
        using var provider = new OidcTestProvider();
        provider.ChangeMetadata = metadata => metadata["authorization_response_iss_parameter_supported"] = required;
        using var http = new HttpClient(provider);
        await using var app = Application(http);
        var (callback, cookie, _) = await Begin(app, provider);
        await using var request = app.Request(callback + (issuer is null ? "" : "&iss=" + Uri.EscapeDataString(issuer)), cookie);
        Assert.Equal("invalid_issuer", (await Assert.ThrowsAsync<OAuthProtocolException>(async () => await request.Context.CompleteOAuthSignInAsync("provider"))).Error);
        Assert.Equal(0, provider.TokenCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DiscoveryDeadlineAndCallerCancellationStopSignIn(bool callerCancellation)
    {
        using var provider = new OidcTestProvider();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.RespondAsync = async (_, ct) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new InvalidOperationException("Unreachable");
        };
        using var http = new HttpClient(provider);
        using var cts = new CancellationTokenSource();
        await using var app = Application(http, options => options.BackchannelTimeout = callerCancellation ? TimeSpan.FromSeconds(10) : TimeSpan.FromMilliseconds(100));
        await using var request = app.Request("https://app.test/login", cancellationToken: cts.Token);
        var begin = request.Context.BeginOAuthSignInAsync("provider").AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (callerCancellation) { cts.Cancel(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => begin);
        Assert.Empty(request.Response.Headers.GetValues(HeaderNames.SetCookie));
    }

    [Theory]
    [InlineData("missing-id-token")]
    [InlineData("empty-keys")]
    [InlineData("symmetric-key")]
    [InlineData("encryption-key")]
    [InlineData("malformed-key")]
    public async Task InvalidProviderTokensAndKeysFailClosed(string failure)
    {
        using var provider = new OidcTestProvider();
        provider.Respond = request =>
        {
            if (failure == "missing-id-token" && request.RequestUri!.AbsolutePath == "/token")
            {
                return OidcTestProvider.Json(new { access_token = "secret", token_type = "Bearer" });
            }
            if (request.RequestUri!.AbsolutePath != "/keys" || failure == "missing-id-token") { return null; }
            return failure switch
            {
                "empty-keys" => OidcTestProvider.Json(new { keys = Array.Empty<object>() }),
                "symmetric-key" => OidcTestProvider.Json(new { keys = new[] { new { kty = "oct", k = "c2VjcmV0" } } }),
                "encryption-key" => OidcTestProvider.Json(new { keys = new[] { new { kty = "RSA", use = "enc" } } }),
                _ => OidcTestProvider.Json(new { keys = new[] { new { kty = "RSA", kid = "first", n = "!invalid!", e = "AQAB" } } }),
            };
        };
        using var http = new HttpClient(provider);
        await using var app = Application(http);
        await Assert.ThrowsAsync<OAuthProtocolException>(async () =>
        {
            var (callback, cookie, _) = await Begin(app, provider);
            await using var complete = app.Request(callback, cookie);
            await complete.Context.CompleteOAuthSignInAsync("provider");
        });
    }

    [Fact]
    public async Task TokensCannotSelectTheirOwnSigningKeySource()
    {
        using var provider = new OidcTestProvider();
        provider.ChangeHeader = header =>
        {
            header["jku"] = "https://attacker.test/keys";
            header["x5u"] = "https://attacker.test/certificate";
        };
        using var http = new HttpClient(provider);
        await using var app = Application(http);
        var (callback, cookie, _) = await Begin(app, provider);
        await using var complete = app.Request(callback, cookie);
        await complete.Context.CompleteOAuthSignInAsync("provider");
        Assert.Equal(1, provider.KeyCalls);
    }

    [Fact]
    public void RegistrationRejectsConflictingEndpointsAndUnsafeAlgorithms()
    {
        using var http = new HttpClient(new OidcTestProvider());
        Assert.Throws<ArgumentException>(() => Application(http, options => options.TokenEndpoint = new Uri("https://other.test/token")));
        Assert.Throws<ArgumentException>(() => Application(http, options => options.OpenIdConnect!.SigningAlgorithms.Add("HS256")));
        Assert.Throws<ArgumentException>(() => Application(http, options => options.OpenIdConnect!.Issuer = "http://provider.test"));
        Assert.Throws<ArgumentException>(() => Application(http, options => options.AuthorizationParameters.Add("nonce", "override")));
        var builder = new ServiceCollection().AddServal().AddAuthentication().AddCookie();
        builder.AddOpenIdConnect("oidc", options =>
        {
            options.ClientId = "client";
            options.RedirectUri = new Uri("https://app.test/callback");
            options.OpenIdConnect!.Issuer = "https://provider.test";
        });
        Assert.Throws<ArgumentException>(() => builder.AddOAuth("oauth", options =>
        {
            options.ClientId = "client";
            options.AuthorizationEndpoint = new Uri("https://provider.test/auth");
            options.TokenEndpoint = new Uri("https://provider.test/token");
            options.RedirectUri = new Uri("https://app.test/callback");
            options.CreatePrincipal = _ => ValueTask.FromResult(SecurityTestApplication.User());
        }));
    }

    private static SecurityTestApplication Application(HttpClient http, Action<OAuthOptions>? configure = null)
        => new(builder => builder.AddAuthentication(options => options.DefaultScheme = "Cookies").AddCookie(configure: options => options.CookieName = "__Host-Serval.Session").AddOpenIdConnect("provider", options =>
        {
            options.ClientId = "client";
            options.RedirectUri = new Uri("https://app.test/callback");
            options.Backchannel = http;
            options.OpenIdConnect!.Issuer = "https://provider.test/tenant";
            options.Scopes.Add("profile");
            configure?.Invoke(options);
        }));

    private static async Task<(string Callback, string Cookie, IReadOnlyDictionary<string, string[]> Query)> Begin(SecurityTestApplication app, OidcTestProvider provider)
    {
        await using var request = app.Request("https://app.test/login");
        await request.Context.BeginOAuthSignInAsync("provider", "/account");
        var query = OAuthClientTests.Query(new Uri(request.Response.Headers[HeaderNames.Location]!));
        provider.Nonce = query["nonce"].Single();
        provider.Now = app.Clock.Now;
        return ("https://app.test/callback?code=code&state=" + query["state"].Single(), SecurityTestApplication.Cookie(request.Response), query);
    }
}
