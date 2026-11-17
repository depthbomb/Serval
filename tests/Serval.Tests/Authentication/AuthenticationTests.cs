#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Security.Claims;
using System.Text;
using Serval.Authentication;
using Serval.Authentication.Authorization;
using Serval.Authentication.Cookies;
using Serval.Hosting;
using Serval.Http;
using Serval.Http.Constants;
using Serval.Http.Results;
using Serval.Sample.OAuth;
using Serval.Tests.Core.Http;
using Serval.Tests.Support;

namespace Serval.Tests.Authentication;

public sealed class AuthenticationTests
{
    [Fact]
    public async Task CookieSignInRoundTripsClaimsPropertiesAndPersistence()
    {
        await using var app   = Cookies();
        await using var login = app.Request();
        await login.Context.SignInAsync(SecurityTestApplication.User("alice", "admin"), properties: new AuthenticationProperties
        {
            IsPersistent = true
        });
        var header = Assert.Single(login.Response.Headers.GetValues(HeaderNames.SetCookie));
        Assert.Contains("Secure", header);
        Assert.Contains("HttpOnly", header);
        Assert.Contains("SameSite=Lax", header);
        Assert.Contains("Expires=", header);
        Assert.DoesNotContain("alice", header);

        await using var request = app.Request(cookie: SecurityTestApplication.Cookie(login.Response));
        var             result  = await request.Context.AuthenticateAsync();
        Assert.True(result.Succeeded);
        Assert.Equal("alice", request.Context.User.Identity!.Name);
        Assert.True(request.Context.User.IsInRole("admin"));
        Assert.Equal(app.Clock.Now.AddHours(8), result.Ticket!.Properties.ExpiresUtc);
        Assert.Equal(await login.Context.GetCsrfTokenAsync(), await request.Context.GetCsrfTokenAsync());
    }

    [Fact]
    public async Task LogoutRevokesCopiedCookieAndSameRequestIdentity()
    {
        await using var app   = Cookies();
        await using var login = app.Request();
        await login.Context.SignInAsync(SecurityTestApplication.User());
        var cookie = SecurityTestApplication.Cookie(login.Response);
        await login.Context.SignOutAsync();
        Assert.True((await login.Context.AuthenticateAsync()).IsMissing);
        Assert.False(login.Context.User.Identity!.IsAuthenticated);
        Assert.Contains(login.Response.Headers.GetValues(HeaderNames.SetCookie), value => value.Contains("Max-Age=0", StringComparison.Ordinal));

        await using var replay = app.Request(cookie: cookie);
        Assert.False((await replay.Context.AuthenticateAsync()).Succeeded);
    }

    [Fact]
    public async Task ReauthenticationRotatesAndRevokesPreviousSession()
    {
        await using var app   = Cookies();
        await using var first = app.Request();
        await first.Context.SignInAsync(SecurityTestApplication.User());
        var             cookie = SecurityTestApplication.Cookie(first.Response);
        await using var second = app.Request(cookie: cookie);
        await second.Context.SignInAsync(SecurityTestApplication.User("bob"));
        Assert.NotEqual(cookie, SecurityTestApplication.Cookie(second.Response));
        await using var replay = app.Request(cookie: cookie);
        Assert.False((await replay.Context.AuthenticateAsync()).Succeeded);
    }

    [Fact]
    public async Task SessionExpirationAndSchemeIsolationAreEnforced()
    {
        await using var app   = new SecurityTestApplication(builder => builder.AddAuthentication(options => options.DefaultScheme = "one").AddCookie("one").AddCookie("two"));
        await using var login = app.Request();
        await login.Context.SignInAsync(SecurityTestApplication.User());
        var             cookie = SecurityTestApplication.Cookie(login.Response);
        await using var other  = app.Request(cookie: cookie);
        Assert.True((await other.Context.AuthenticateAsync("two")).IsMissing);
        app.Clock.Now += TimeSpan.FromHours(8);
        await using var expired = app.Request(cookie: cookie);
        Assert.False((await expired.Context.AuthenticateAsync()).Succeeded);
    }

    [Fact]
    public async Task StoredPrincipalsAreIsolatedFromRequestMutation()
    {
        await using var app       = Cookies();
        var             principal = SecurityTestApplication.User();
        await using var login     = app.Request();
        await login.Context.SignInAsync(principal);
        ((ClaimsIdentity)principal.Identity!).AddClaim(new Claim(ClaimTypes.Role, "admin"));
        ((ClaimsIdentity)login.Context.User.Identity!).AddClaim(new Claim(ClaimTypes.Role, "admin"));
        await using var request = app.Request(cookie: SecurityTestApplication.Cookie(login.Response));
        await request.Context.AuthenticateAsync();
        Assert.False(request.Context.User.IsInRole("admin"));
    }

    [Fact]
    public async Task SlidingRenewalCannotExceedAbsoluteLifetime()
    {
        await using var app = Cookies(options =>
        {
            options.Lifetime          = TimeSpan.FromMinutes(10);
            options.MaximumLifetime   = TimeSpan.FromMinutes(15);
            options.SlidingExpiration = true;
        });
        var             started = app.Clock.Now;
        await using var login   = app.Request();
        await login.Context.SignInAsync(SecurityTestApplication.User(), properties: new AuthenticationProperties
        {
            IsPersistent = true
        });
        var cookie = SecurityTestApplication.Cookie(login.Response);
        app.Clock.Now += TimeSpan.FromMinutes(6);
        await using var renew  = app.Request(cookie: cookie);
        var             ticket = (await renew.Context.AuthenticateAsync()).Ticket!;
        Assert.Equal(started.AddMinutes(15), ticket.Properties.ExpiresUtc);
        Assert.Single(renew.Response.Headers.GetValues(HeaderNames.SetCookie));
        app.Clock.Now = started.AddMinutes(15);
        await using var expired = app.Request(cookie: cookie);
        Assert.False((await expired.Context.AuthenticateAsync()).Succeeded);
    }

    [Fact]
    public async Task ValidationHookCanRevokeAccountsAndRunsOncePerRequest()
    {
        var calls    = 0;
        var disabled = false;
        await using var app = Cookies(options => options.ValidateTicket = (_, ticket) =>
        {
            calls++;
            return ValueTask.FromResult(disabled ? AuthenticationResult.Fail("disabled") : AuthenticationResult.Success(ticket));
        });
        await using var login = app.Request();
        await login.Context.SignInAsync(SecurityTestApplication.User());
        var             cookie = SecurityTestApplication.Cookie(login.Response);
        await using var first  = app.Request(cookie: cookie);
        await first.Context.AuthenticateAsync();
        await first.Context.AuthenticateAsync();
        Assert.Equal(1, calls);
        disabled = true;
        await using var rejected = app.Request(cookie: cookie);
        Assert.False((await rejected.Context.AuthenticateAsync()).Succeeded);
        disabled = false;
        await using var replay = app.Request(cookie: cookie);
        Assert.False((await replay.Context.AuthenticateAsync()).Succeeded);
    }

    [Fact]
    public async Task SecureCookiesRejectHttpAndStartedResponses()
    {
        await using var app  = Cookies();
        await using var http = app.Request("http://app.test/");
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await http.Context.SignInAsync(SecurityTestApplication.User()));
        await using var started = app.Request();
        await started.Response.StartAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await started.Context.SignInAsync(SecurityTestApplication.User()));
    }

    [Fact]
    public async Task CsrfMiddlewareRequiresTheTokenForTheCurrentSession()
    {
        await using var app   = Cookies();
        await using var login = app.Request();
        await login.Context.SignInAsync(SecurityTestApplication.User());
        var token  = await login.Context.GetCsrfTokenAsync();
        var cookie = SecurityTestApplication.Cookie(login.Response);
        foreach (var supplied in new[] { null, "wrong", token })
        {
            await using var request = app.Request(cookie: cookie, method: "POST", headers: headers => headers["X-CSRF-Token"] = supplied);
            var             invoked = false;
            await new RequireCsrfAttribute().InvokeAsync(request.Context, _ =>
            {
                invoked = true;
                return ValueTask.CompletedTask;
            });
            Assert.Equal(supplied == token, invoked);
            Assert.Equal(supplied == token ? 200 : 403, request.Response.StatusCode);
        }
    }

    [Theory]
    [InlineData(false, "valid", true)]
    [InlineData(true, "valid", true)]
    [InlineData(false, "duplicate", false)]
    [InlineData(true, "duplicate", false)]
    [InlineData(false, "wrong-header", false)]
    [InlineData(true, "wrong-header", false)]
    [InlineData(false, "disabled", false)]
    public async Task CsrfAcceptsSingleFormTokensWithoutOverridingHeaders(bool multipart, string scenario, bool expected)
    {
        await using var app   = Cookies();
        await using var login = app.Request();
        await login.Context.SignInAsync(SecurityTestApplication.User());
        var token  = await login.Context.GetCsrfTokenAsync();
        var cookie = SecurityTestApplication.Cookie(login.Response);
        var fields = new List<(string Name, string Value)>
        {
            ("_csrf", token),
            ("value", "still readable")
        };
        if (scenario == "duplicate")
        {
            fields.Add(("_csrf", token));
        }

        var body = multipart ? FormDataTests.Multipart([.. fields]) : string.Join('&', fields.Select(field => field.Name + "=" + Uri.EscapeDataString(field.Value)));
        var headers = new Headers
        {
            [HeaderNames.ContentType] = multipart ? "multipart/form-data; boundary=example" : MimeTypes.FormUrlEncoded,
            [HeaderNames.Cookie]      = cookie,
        };
        if (scenario == "wrong-header")
        {
            headers[HeaderNames.XCsrfToken] = "wrong";
        }

        await using var request = new Request("POST", new Uri("https://app.test/"), HttpVersion.Version11, headers,
            new MemoryStream(Encoding.UTF8.GetBytes(body)), 4096);
        await using var scope   = app.Request(cookie: cookie);
        var             context = new RequestContext(request, scope.Response, scope.Context.Services, scope.Context.Urls, CancellationToken.None);
        var             invoked = false;
        await new RequireCsrfAttribute
        {
            FormFieldName = scenario == "disabled" ? null : "_csrf"
        }.InvokeAsync(context, async current =>
        {
            invoked = true;
            Assert.Equal("still readable", (await current.Request.ReadFormAsync()).Get<string>("value"));
        });
        Assert.Equal(expected, invoked);
        Assert.Equal(expected ? 200 : 403, scope.Response.StatusCode);
    }

    [Fact]
    public async Task AuthorizationDistinguishesChallengeForbiddenAndSuccessInPipeline()
    {
        await using var app = new SecurityTestApplication(builder =>
        {
            builder.AddAuthentication(options =>
            {
                options.DefaultScheme = "Cookies";
                options.Policies.Add("admin", new AuthorizationPolicy().RequireRole("admin").RequireClaim(ClaimTypes.NameIdentifier));
            }).AddCookie();
            builder.AddController<object>("GET", "/private", null, (_, context) => ValueTask.FromResult<IActionResult>(new TextResult(context.User.Identity!.Name!)), new AuthorizeAttribute
            {
                Policy = "admin"
            });
        });
        foreach (var role in new[] { null, "member", "admin" })
        {
            string? cookie = null;
            if (role is not null)
            {
                await using var login = app.Request();
                await login.Context.SignInAsync(SecurityTestApplication.User("alice", role));
                cookie = SecurityTestApplication.Cookie(login.Response);
            }

            await using var request = app.Request("https://app.test/private", cookie);
            await app.Services.GetRequiredService<IRequestHandler>().HandleAsync(request.Context.Request, request.Response, default);
            Assert.Equal(role is null ? 401 : role == "admin" ? 200 : 403, request.Response.StatusCode);
            Assert.Equal(role                      == "admin" ? "alice" : "", request.Response.Text);
            await request.Context.AuthenticateAsync();
            Assert.Equal(role == "admin", await request.Context.AuthorizeAsync("admin"));
        }
    }

    [Theory]
    [InlineData(null, 0, false)]
    [InlineData("Basic credentials", 0, false)]
    [InlineData("Bearer", 0, false)]
    [InlineData("Bearer token, other", 0, false)]
    [InlineData("Bearer token", 1, true)]
    public async Task BearerValidationIsExplicitAndRejectsAmbiguousHeaders(string? header, int expectedCalls, bool success)
    {
        var calls = 0;
        await using var app = new SecurityTestApplication(builder => builder.AddAuthentication(options => options.DefaultScheme = "api").AddBearer("api", options => options.ValidateToken = (_, token) =>
        {
            Assert.Equal("token", token);
            calls++;
            return ValueTask.FromResult(SecurityTestApplication.Success("api"));
        }));
        await using var request = app.Request(headers: headers => headers[HeaderNames.Authorization] = header);
        Assert.Equal(success, (await request.Context.AuthenticateAsync()).Succeeded);
        Assert.Equal(expectedCalls, calls);
        await request.Context.ChallengeAsync();
        Assert.Equal(401, request.Response.StatusCode);
        Assert.Equal("Bearer", request.Response.Headers[HeaderNames.WwwAuthenticate]);
    }

    [Fact]
    public async Task MissingSchemesAndPoliciesFailClosed()
    {
        await using var app     = Cookies();
        await using var request = app.Request();
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await request.Context.AuthenticateAsync("missing"));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await request.Context.Services.GetRequiredService<IAuthorizationService>().AuthorizeAsync(request.Context, SecurityTestApplication.User(), "missing"));
    }

    [Fact]
    public async Task BearerHandlerRejectsCleartextAndRepeatedCredentialsBeforeValidation()
    {
        var calls = 0;
        await using var app = new SecurityTestApplication(builder => builder.AddAuthentication(options => options.DefaultScheme = "api").AddBearer("api", options => options.ValidateToken = (_, _) =>
        {
            calls++;
            return ValueTask.FromResult(SecurityTestApplication.Success("api"));
        }));
        await using var http = app.Request("http://app.test/", headers: headers => headers[HeaderNames.Authorization] = "Bearer token");
        Assert.False((await http.Context.AuthenticateAsync()).Succeeded);
        await using var repeated = app.Request(headers: headers =>
        {
            headers.Append(HeaderNames.Authorization, "Bearer one");
            headers.Append(HeaderNames.Authorization, "Bearer two");
        });
        Assert.False((await repeated.Context.AuthenticateAsync()).Succeeded);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task MemoryStoreBoundsCapacityAndCannotRenewRevokedSessions()
    {
        var time = new TestTimeProvider();
        var store = new MemoryAuthenticationSessionStore(time, new MemoryAuthenticationSessionStoreOptions
        {
            Capacity = 1
        });
        var ticket = new AuthenticationTicket(SecurityTestApplication.User(), "Cookies", new AuthenticationProperties
        {
            ExpiresUtc = time.Now.AddMinutes(1)
        });
        await store.StoreAsync("a", ticket);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await store.StoreAsync("b", ticket));
        await store.RemoveAsync("a");
        Assert.False(await store.RenewAsync("a", ticket));
        await store.StoreAsync("b", ticket);
        time.Now += TimeSpan.FromMinutes(1);
        Assert.Null(await store.FindAsync("b"));
        await store.StoreAsync("c", ticket with
        {
            Properties = ticket.Properties with
            {
                ExpiresUtc = time.Now.AddMinutes(1)
            }
        });
    }

    [Fact]
    public void InvalidCookieSettingsAndDuplicateRegistrationAreRejected()
    {
        var builder        = new ServiceCollection().AddServal();
        var authentication = builder.AddAuthentication();
        authentication.AddCookie();
        Assert.Throws<ArgumentException>(() => authentication.AddCookie());
        Assert.Throws<InvalidOperationException>(() => builder.AddAuthentication());
        Assert.Throws<ArgumentException>(() => authentication.AddCookie("bad", options => options.Cookie = new CookieOptions
        {
            Secure = false
        }));
    }

    [Fact]
    public async Task CustomHandlersUseRequestScopesAndAreDisposed()
    {
        var disposed = 0;
        await using var app = new SecurityTestApplication(builder => builder.AddAuthentication(options => options.DefaultScheme = "custom")
                                                                            .AddScheme("custom", _ => new CustomHandler(() => disposed++)));
        string? first;
        await using (var request = app.Request())
        {
            var result = await request.Context.AuthenticateAsync();
            first = result.Ticket!.Principal.Identity!.Name;
            Assert.Same(result, await request.Context.AuthenticateAsync());
        }

        Assert.Equal(1, disposed);
        await using (var request = app.Request())
        {
            var result = await request.Context.AuthenticateAsync();
            Assert.NotEqual(first, result.Ticket!.Principal.Identity!.Name);
        }

        Assert.Equal(2, disposed);
    }

    [Fact]
    public async Task PersistenceAndRenewalCanBeDisabledPerSession()
    {
        await using var app   = Cookies(options => options.SlidingExpiration = true);
        await using var login = app.Request();
        await login.Context.SignInAsync(SecurityTestApplication.User(), properties: new AuthenticationProperties
        {
            AllowRefresh = false
        });
        Assert.DoesNotContain("Expires=", login.Response.Headers.GetValues(HeaderNames.SetCookie).Single());
        app.Clock.Now += TimeSpan.FromHours(5);
        await using var request = app.Request(cookie: SecurityTestApplication.Cookie(login.Response));
        Assert.True((await request.Context.AuthenticateAsync()).Succeeded);
        Assert.Empty(request.Response.Headers.GetValues(HeaderNames.SetCookie));
    }

    [Fact]
    public async Task AuthenticationSampleUsesCsrfForLogout()
    {
        using var http = new HttpClient(new OAuthTestBackchannel());
        await using var app = new SecurityTestApplication(builder => OAuthSample.Configure(builder, options =>
        {
            options.ClientId              = "test";
            options.AuthorizationEndpoint = new Uri("https://provider.test/authorize");
            options.TokenEndpoint         = new Uri("https://provider.test/token");
            options.RedirectUri           = new Uri("https://app.test/auth/callback");
            options.Backchannel           = http;
            options.CreatePrincipal       = _ => ValueTask.FromResult(SecurityTestApplication.User());
        }));
        await using var login = app.Request();
        await login.Context.SignInAsync(SecurityTestApplication.User());
        var             cookie   = SecurityTestApplication.Cookie(login.Response);
        var             token    = await login.Context.GetCsrfTokenAsync();
        await using var rejected = app.Request("https://app.test/auth/logout", cookie, "POST");
        await app.Services.GetRequiredService<IRequestHandler>().HandleAsync(rejected.Context.Request, rejected.Response, default);
        Assert.Equal(403, rejected.Response.StatusCode);
        await using var accepted = app.Request("https://app.test/auth/logout", cookie, "POST", headers => headers["X-CSRF-Token"] = token);
        await app.Services.GetRequiredService<IRequestHandler>().HandleAsync(accepted.Context.Request, accepted.Response, default);
        Assert.Equal(204, accepted.Response.StatusCode);
        await using var replay = app.Request(cookie: cookie);
        Assert.False((await replay.Context.AuthenticateAsync()).Succeeded);
    }

    private static SecurityTestApplication Cookies(Action<CookieAuthenticationOptions>? configure = null)
        => new(builder => builder.AddAuthentication(options => options.DefaultScheme = "Cookies").AddCookie(configure: configure));

    private sealed class CustomHandler : AuthenticationHandler, IDisposable
    {
        private readonly string _id = Guid.NewGuid().ToString();
        private readonly Action _onDispose;

        public CustomHandler(Action onDispose)
        {
            _onDispose = onDispose;
        }

        public override ValueTask<AuthenticationResult> AuthenticateAsync(RequestContext context, string scheme)
            => ValueTask.FromResult(AuthenticationResult.Success(new AuthenticationTicket(SecurityTestApplication.User(_id), scheme, new AuthenticationProperties())));

        public void Dispose() => _onDispose();
    }
}
