using Microsoft.Extensions.DependencyInjection;
using Serval.Authentication;
using Serval.Authentication.Authorization;
using Serval.Authentication.Cookies;
using Serval.Authentication.Passwords;
using Serval.Tests.Support;

namespace Serval.Tests.Authentication;

public sealed class CompositionTests
{
    [Fact]
    public async Task ExistingAuthenticationServicesAndPasswordOptionsArePreserved()
    {
        var custom = new AuthenticationService(new AuthenticationOptions());
        await using var app = new SecurityTestApplication(builder =>
        {
            builder.Services.AddScoped<IAuthenticationService>(_ => custom);
            builder.AddAuthenticationCore();
            builder.AddPasswordHashing();
            builder.AddPasswordHashing(options => options.Iterations = 123_456);
        });
        await using var request = app.Request();
        Assert.Same(custom, request.Context.Services.GetRequiredService<IAuthenticationService>());
        Assert.Equal(123_456, request.Context.Services.GetRequiredService<Pbkdf2PasswordHasherOptions>().Iterations);
    }

    [Fact]
    public async Task EvaluationCachesResultsWithoutChangingTheChosenPrincipal()
    {
        var calls = 0;
        await using var app = new SecurityTestApplication(builder => builder.AddAuthenticationCore().AddBearer("token", options =>
            options.ValidateToken = (_, _) =>
            {
                calls++;
                return ValueTask.FromResult(SecurityTestApplication.Success("token"));
            }).AddBearer("missing", options => options.ValidateToken = (_, _) => ValueTask.FromResult(AuthenticationResult.Fail("rejected"))));
        await using var request = app.Request(headers: headers => headers["Authorization"] = "Bearer value");
        request.Context.User = SecurityTestApplication.User("original");
        var result = await request.Context.EvaluateAuthenticationAsync("token");
        Assert.Same(result, await request.Context.EvaluateAuthenticationAsync("token"));
        Assert.False((await request.Context.EvaluateAuthenticationAsync("missing")).Succeeded);
        Assert.Equal("original", request.Context.User.Identity!.Name);
        Assert.Equal(1, calls);
        request.Context.ApplyAuthentication(result);
        Assert.Equal("alice", request.Context.User.Identity!.Name);
    }

    [Fact]
    public async Task RegistrationPiecesCanBeUsedIndependently()
    {
        await using var auth    = new SecurityTestApplication(builder => builder.AddAuthenticationCore());
        await using var request = auth.Request();
        Assert.NotNull(request.Context.Services.GetService<IAuthenticationService>());
        Assert.Null(request.Context.Services.GetService<IAuthorizationService>());
        Assert.Null(request.Context.Services.GetService<IPasswordHasher>());

        await using var policies      = new SecurityTestApplication(builder => builder.AddAuthorization().AddPasswordHashing());
        await using var policyRequest = policies.Request();
        Assert.Null(policyRequest.Context.Services.GetService<IAuthenticationService>());
        Assert.NotNull(policyRequest.Context.Services.GetService<IAuthorizationService>());
        Assert.NotNull(policyRequest.Context.Services.GetService<IPasswordHasher>());
    }

    [Fact]
    public async Task SessionsRotateAndRenewWithoutCookiesAndCannotReviveRevokedCredentials()
    {
        var clock = new TestTimeProvider();
        var store = new MemoryAuthenticationSessionStore(clock, new MemoryAuthenticationSessionStoreOptions());
        var sessions = new AuthenticationSessionService(store, clock, new AuthenticationSessionOptions
        {
            Lifetime        = TimeSpan.FromMinutes(10),
            MaximumLifetime = TimeSpan.FromMinutes(15),
        });
        var first = await sessions.IssueAsync("custom", SecurityTestApplication.User());
        Assert.False((await sessions.ValidateAsync("other", first.Credential)).Succeeded);
        clock.Now += TimeSpan.FromMinutes(8);
        var renewed = await sessions.RenewAsync("custom", first.Credential);
        Assert.Equal(first.Ticket.Properties.IssuedUtc!.Value.AddMinutes(15), renewed.Ticket!.Properties.ExpiresUtc);
        var second = await sessions.IssueAsync("custom", SecurityTestApplication.User("bob"), previousCredential: first.Credential);
        Assert.False((await sessions.ValidateAsync("custom", first.Credential)).Succeeded);
        Assert.False((await sessions.RenewAsync("custom", first.Credential)).Succeeded);
        Assert.Equal("bob", (await sessions.ValidateAsync("custom", second.Credential)).Ticket!.Principal.Identity!.Name);
        Assert.DoesNotContain(second.Credential, second.ToString());
    }

    [Fact]
    public void CsrfStateSupportsAnonymousBrowsersAndRotation()
    {
        var browser = new Dictionary<string, string>();
        var token   = CsrfTokens.Rotate(browser);
        Assert.True(CsrfTokens.Validate(browser, token));
        Assert.False(CsrfTokens.Validate(new Dictionary<string, string>(), token));
        Assert.False(CsrfTokens.Validate(browser, null));
        Assert.False(CsrfTokens.Validate(browser, new string('é', 43)));
        CsrfTokens.Rotate(browser);
        Assert.False(CsrfTokens.Validate(browser, token));
    }
}
