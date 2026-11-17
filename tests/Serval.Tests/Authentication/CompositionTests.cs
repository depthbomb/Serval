using Microsoft.Extensions.DependencyInjection;
using Serval.Authentication;
using Serval.Authentication.Authorization;
using Serval.Authentication.Cookies;
using Serval.Authentication.Passwords;
using Serval.Hosting;
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
    public async Task RegistrationPiecesCanBeUsedIndependently()
    {
        await using var auth = new SecurityTestApplication(builder => builder.AddAuthenticationCore());
        await using var request = auth.Request();
        Assert.NotNull(request.Context.Services.GetService<IAuthenticationService>());
        Assert.Null(request.Context.Services.GetService<IAuthorizationService>());
        Assert.Null(request.Context.Services.GetService<IPasswordHasher>());

        await using var policies = new SecurityTestApplication(builder => builder.AddAuthorization().AddPasswordHashing());
        await using var policyRequest = policies.Request();
        Assert.Null(policyRequest.Context.Services.GetService<IAuthenticationService>());
        Assert.NotNull(policyRequest.Context.Services.GetService<IAuthorizationService>());
        Assert.NotNull(policyRequest.Context.Services.GetService<IPasswordHasher>());
    }
}
