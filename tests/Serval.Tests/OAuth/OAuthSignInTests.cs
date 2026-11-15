#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.Extensions.DependencyInjection;
using System.Security.Claims;
using Serval.Authentication;
using Serval.Hosting;
using Serval.Http.Constants;
using Serval.OAuth;
using Serval.Tests.Support;

namespace Serval.Tests.OAuth;

public sealed class OAuthSignInTests
{
    [Fact]
    public async Task BrowserFlowBindsPkceMapsProviderIdentityAndIssuesLocalSession()
    {
        using var       handler = new OAuthTestBackchannel();
        using var       http    = new HttpClient(handler);
        await using var app     = Application(http);
        await using var begin   = app.Request("https://app.test/login");
        await begin.Context.BeginOAuthSignInAsync("provider", "/search?q=hello%20world", new AuthenticationProperties { IsPersistent = true });
        var authorization = new Uri(begin.Response.Headers[HeaderNames.Location]!);
        var query         = OAuthClientTests.Query(authorization);
        var cookie        = SecurityTestApplication.Cookie(begin.Response);
        Assert.Contains("HttpOnly", begin.Response.Headers.GetValues(HeaderNames.SetCookie).Single());
        Assert.Equal("no-store", begin.Response.Headers[HeaderNames.CacheControl]);
        Assert.Equal("no-referrer", begin.Response.Headers["Referrer-Policy"]);

        var             callback = "https://app.test/callback?code=code&state=" + query["state"].Single();
        await using var complete = app.Request(callback, cookie);
        var             result   = await complete.Context.CompleteOAuthSignInAsync("provider");
        Assert.Equal("/search?q=hello%20world", result.ReturnUrl);
        Assert.Equal("Ada", result.Principal.Identity!.Name);
        Assert.Equal("provider-user", result.Principal.FindFirst(ClaimTypes.NameIdentifier)!.Value);
        Assert.Equal("no-store", complete.Response.Headers[HeaderNames.CacheControl]);
        var form = OAuthClientTests.Form(handler.Requests.First().Body);
        Assert.Equal(query["code_challenge"].Single(), OAuthClientTests.Challenge(form["code_verifier"].Single()));
        Assert.DoesNotContain("access-secret", string.Join(';', complete.Response.Headers.GetValues(HeaderNames.SetCookie)));

        await using var signedIn = app.Request(cookie: SecurityTestApplication.Cookie(complete.Response, "__Host-Serval.Session="));
        Assert.True((await signedIn.Context.AuthenticateAsync()).Succeeded);
        Assert.Equal("Ada", signedIn.Context.User.Identity!.Name);
        await using var replay = app.Request(callback, cookie);
        Assert.Equal("invalid_state", (await Assert.ThrowsAsync<OAuthProtocolException>(async () => await replay.Context.CompleteOAuthSignInAsync("provider"))).Error);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task MissingOrWrongBrowserCookieDoesNotConsumeLegitimateState()
    {
        using var       handler = new OAuthTestBackchannel();
        using var       http    = new HttpClient(handler);
        await using var app     = Application(http);
        var (state, cookie) = await Begin(app);
        var             callback = "https://app.test/callback?code=code&state=" + state;
        await using var missing  = app.Request(callback);
        Assert.Equal("invalid_correlation", (await Assert.ThrowsAsync<OAuthProtocolException>(async () => await missing.Context.CompleteOAuthSignInAsync("provider"))).Error);
        await using var wrong = app.Request(callback, cookie[..(cookie.IndexOf('=') + 1)] + new string('x', 43));
        Assert.Equal("invalid_state", (await Assert.ThrowsAsync<OAuthProtocolException>(async () => await wrong.Context.CompleteOAuthSignInAsync("provider"))).Error);
        Assert.Empty(handler.Requests);
        await using var valid = app.Request(callback, cookie);
        await valid.Context.CompleteOAuthSignInAsync("provider");
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task ParallelCallbacksCanOnlyExchangeTheCodeOnce()
    {
        using var       handler = new OAuthTestBackchannel();
        using var       http    = new HttpClient(handler);
        await using var app     = Application(http);
        var (state, cookie) = await Begin(app);
        var successes = 0;
        await Task.WhenAll(Enumerable.Range(0, 10).Select(async _ =>
        {
            await using var request = app.Request("https://app.test/callback?code=code&state=" + state, cookie);
            try
            {
                await request.Context.CompleteOAuthSignInAsync("provider");
                Interlocked.Increment(ref successes);
            }
            catch (OAuthProtocolException exception)
            {
                Assert.Equal("invalid_state", exception.Error);
            }
        }));
        Assert.Equal(1, successes);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Theory]
    [InlineData("&state=duplicate", "invalid_response")]
    [InlineData("&code=duplicate", "invalid_response")]
    [InlineData("&error=access_denied", "invalid_response")]
    [InlineData("&iss=unexpected", "invalid_issuer")]
    public async Task AmbiguousAndMismatchedCallbacksNeverContactProvider(string extra, string expected)
    {
        using var       handler = new OAuthTestBackchannel();
        using var       http    = new HttpClient(handler);
        await using var app     = Application(http, options => options.ExpectedIssuer = "https://provider.test/");
        var (state, cookie) = await Begin(app);
        var             issuer   = extra.StartsWith("&iss", StringComparison.Ordinal) ? "" : "&iss=https%3A%2F%2Fprovider.test%2F";
        await using var callback = app.Request("https://app.test/callback?code=code&state=" + state + issuer + extra, cookie);
        Assert.Equal(expected, (await Assert.ThrowsAsync<OAuthProtocolException>(async () => await callback.Context.CompleteOAuthSignInAsync("provider"))).Error);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ProviderDenialConsumesTheStateAndDeletesCorrelationCookie()
    {
        using var       handler = new OAuthTestBackchannel();
        using var       http    = new HttpClient(handler);
        await using var app     = Application(http);
        var (state, cookie) = await Begin(app);
        await using var callback = app.Request("https://app.test/callback?error=access_denied&state=" + state, cookie);
        Assert.Equal("access_denied", (await Assert.ThrowsAsync<OAuthProtocolException>(async () => await callback.Context.CompleteOAuthSignInAsync("provider"))).Error);
        Assert.Contains("Max-Age=0", callback.Response.Headers.GetValues(HeaderNames.SetCookie).Single());
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ExpiredStateAndWrongCallbackOriginAreRejected()
    {
        using var       handler = new OAuthTestBackchannel();
        using var       http    = new HttpClient(handler);
        await using var app     = Application(http);
        var (state, cookie) = await Begin(app);
        await using var wrong = app.Request("https://other.test/callback?code=code&state=" + state, cookie);
        Assert.Equal("invalid_callback", (await Assert.ThrowsAsync<OAuthProtocolException>(async () => await wrong.Context.CompleteOAuthSignInAsync("provider"))).Error);
        app.Clock.Now += TimeSpan.FromMinutes(10);
        await using var expired = app.Request("https://app.test/callback?code=code&state=" + state, cookie);
        Assert.Equal("invalid_state", (await Assert.ThrowsAsync<OAuthProtocolException>(async () => await expired.Context.CompleteOAuthSignInAsync("provider"))).Error);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("https://attacker.test/")]
    [InlineData("//attacker.test/")]
    [InlineData("/\\attacker.test/")]
    [InlineData("/%2f/attacker.test/")]
    [InlineData("/\r\nLocation:evil")]
    public async Task ReturnUrlsCannotCreateOpenRedirects(string returnUrl)
    {
        using var       http    = new HttpClient(new OAuthTestBackchannel());
        await using var app     = Application(http);
        await using var request = app.Request();
        await Assert.ThrowsAsync<ArgumentException>(async () => await request.Context.BeginOAuthSignInAsync("provider", returnUrl));
        Assert.Empty(request.Response.Headers.GetValues(HeaderNames.SetCookie));
    }

    [Fact]
    public async Task MappingMustProduceAnAuthenticatedIdentityBeforeSessionCreation()
    {
        using var       http = new HttpClient(new OAuthTestBackchannel());
        await using var app  = Application(http, options => options.CreatePrincipal = _ => ValueTask.FromResult(new ClaimsPrincipal(new ClaimsIdentity())));
        var (state, cookie) = await Begin(app);
        await using var request = app.Request("https://app.test/callback?code=code&state=" + state, cookie);
        Assert.Equal("invalid_identity", (await Assert.ThrowsAsync<OAuthProtocolException>(async () => await request.Context.CompleteOAuthSignInAsync("provider"))).Error);
        Assert.DoesNotContain(request.Response.Headers.GetValues(HeaderNames.SetCookie), value => value.StartsWith("__Host-Serval.Session=", StringComparison.Ordinal));
    }

    [Fact]
    public void ProvidersRequireDistinctCallbacksAndExplicitIdentityMapping()
    {
        using var http    = new HttpClient(new OAuthTestBackchannel());
        var       builder = new ServiceCollection().AddServal().AddAuthentication().AddCookie();
        builder.AddOAuth("one", options => Configure(options, http));
        Assert.Throws<ArgumentException>(() => builder.AddOAuth("two", options => Configure(options, http)));
        Assert.Throws<ArgumentNullException>(() => builder.AddOAuth("three", options =>
        {
            Configure(options, http);
            options.CreatePrincipal = null;
        }));
    }

    [Fact]
    public async Task StateStoreIsBoundedAndChecksSchemeBeforeConsuming()
    {
        var time  = new TestTimeProvider();
        var store = new MemoryOAuthStateStore(time, new MemoryOAuthStateStoreOptions { Capacity = 1 });
        var state = new OAuthState
        {
            Scheme          = "one",
            CorrelationHash = "binding",
            CodeVerifier    = "verifier",
            RedirectUri     = new Uri("https://app.test/callback"),
            ReturnUrl       = "/",
            ExpiresUtc      = time.Now.AddMinutes(1),
        };
        await store.StoreAsync("key", state);
        Assert.Null(await store.TakeAsync("key", "two", "binding"));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await store.StoreAsync("other", state));
        Assert.NotNull(await store.TakeAsync("key", "one", "binding"));
        Assert.Null(await store.TakeAsync("key", "one", "binding"));
        await store.StoreAsync("other", state);
        time.Now += TimeSpan.FromMinutes(1);
        Assert.Null(await store.TakeAsync("other", "one", "binding"));
    }

    [Fact]
    public async Task NamedClientCanBeUsedWithoutAuthenticationRegistration()
    {
        using var http = new HttpClient(new OAuthTestBackchannel());
        await using var app = new SecurityTestApplication(builder => builder.AddOAuthClient("service", options =>
        {
            options.ClientId      = "service";
            options.TokenEndpoint = new Uri("https://provider.test/token");
            options.Backchannel   = http;
        }));
        var tokens = await app.Services.GetRequiredKeyedService<OAuthClient>("service").ClientCredentialsAsync();
        Assert.Equal("access-secret", tokens.AccessToken);
    }

    private static SecurityTestApplication Application(HttpClient http, Action<OAuthOptions>? configure = null)
        => new(builder => builder.AddAuthentication(options => options.DefaultScheme = "Cookies").AddCookie(configure: options => options.CookieName = "__Host-Serval.Session").AddOAuth("provider", options =>
        {
            Configure(options, http);
            configure?.Invoke(options);
        }));

    private static void Configure(OAuthOptions options, HttpClient http)
    {
        options.ClientId                = "client";
        options.AuthorizationEndpoint   = new Uri("https://provider.test/authorize");
        options.TokenEndpoint           = new Uri("https://provider.test/token");
        options.UserInformationEndpoint = new Uri("https://provider.test/profile");
        options.RedirectUri             = new Uri("https://app.test/callback");
        options.Backchannel             = http;
        options.CreatePrincipal = async context =>
        {
            var profile = await context.Client.GetUserInformationAsync(context.Tokens, context.Request.RequestAborted);
            return new ClaimsPrincipal(new ClaimsIdentity([
                new Claim(ClaimTypes.NameIdentifier, profile.GetProperty("id").GetString()!),
                new Claim(ClaimTypes.Name, profile.GetProperty("name").GetString()!),
            ], context.Scheme));
        };
    }

    private static async Task<(string State, string Cookie)> Begin(SecurityTestApplication app)
    {
        await using var request = app.Request("https://app.test/login");
        await request.Context.BeginOAuthSignInAsync("provider");
        var state = OAuthClientTests.Query(new Uri(request.Response.Headers[HeaderNames.Location]!))["state"].Single();
        return (state, SecurityTestApplication.Cookie(request.Response));
    }
}
