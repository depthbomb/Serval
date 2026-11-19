#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Serval.Authentication;
using Serval.Authentication.Authorization;
using Serval.Authentication.Cookies;
using Serval.Http;
using Serval.Http.Constants;
using Serval.OAuth;
using Serval.Routing;

namespace Serval.Sample.OAuth;

[Controller]
public sealed class SignInController : ControllerBase
{
    private readonly ProviderStatus _provider;

    public SignInController(ProviderStatus provider)
    {
        _provider = provider;
    }

    [Get("/")]
    public ValueTask<IActionResult> IndexAsync(RequestContext context) => TextAsync($"""
                                                                                     OAuth/OIDC authorization-code sign-in with PKCE and server-side cookie sessions
                                                                                     Provider configured: {_provider.Configured}
                                                                                     Set environment variables before starting:
                                                                                       OAuth__ClientId and OAuth__Issuer for OIDC discovery and validated ID tokens.
                                                                                     For plain OAuth, replace Issuer with OAuth__AuthorizationEndpoint,
                                                                                       OAuth__TokenEndpoint, OAuth__UserInformationEndpoint.
                                                                                     Optional: OAuth__ClientSecret, OAuth__ClientAuthentication (ClientSecretBasic or
                                                                                     ClientSecretPost), OAuth__Scopes (space separated), OAuth__ExpectedIssuer,
                                                                                     OAuth__IdClaim (default sub), OAuth__NameClaim (default name).
                                                                                     Register http://localhost:{context.Request.Url.Port}/auth/callback with your provider.
                                                                                     GET /auth/login starts sign-in. GET /auth/me shows the local identity.
                                                                                     GET /auth/csrf returns a token for POST /auth/logout (X-CSRF-Token header).
                                                                                     Missing provider configuration makes /auth/login return 503.
                                                                                     Tokens stay on the server. OIDC uses validated ID-token claims; plain OAuth
                                                                                     uses the provider's profile endpoint. Local logout revokes only the
                                                                                     local session, not the provider session or grants. Use HTTPS outside loopback.
                                                                                     """);

    [Get("/auth/login")]
    public async ValueTask<IActionResult> LoginAsync(RequestContext context)
    {
        AbortUnless(_provider.Configured, StatusCodes.ServiceUnavailable, "Configure the OAuth provider described at / first.");

        await context.BeginOAuthSignInAsync("provider", "/auth/me");

        return Empty();
    }

    [Get("/auth/callback")]
    public async ValueTask<IActionResult> CallbackAsync(RequestContext context)
    {
        AbortUnless(_provider.Configured, StatusCodes.ServiceUnavailable, "Configure the OAuth provider described at / first.");

        try
        {
            var signIn = await context.CompleteOAuthSignInAsync("provider");
            return Redirect(signIn.ReturnUrl);
        }
        catch (OAuthProtocolException)
        {
            return Text("Sign-in could not be completed. Start a new sign-in attempt.", StatusCodes.BadRequest);
        }
    }

    [Get("/auth/me")]
    [Authorize]
    public ValueTask<IActionResult> MeAsync(RequestContext context)
    {
        context.Response.Headers[HeaderNames.CacheControl] = "no-store";
        return JsonAsync(new
        {
            name = context.User.Identity?.Name,
        });
    }

    [Get("/auth/csrf")]
    [Authorize]
    public async ValueTask<IActionResult> CsrfAsync(RequestContext context)
        => Json(new
        {
            token = await context.GetCsrfTokenAsync(),
        });

    [Post("/auth/logout")]
    [Authorize(Priority = -100)]
    [RequireCsrf]
    public async ValueTask<IActionResult> LogoutAsync(RequestContext context)
    {
        await context.SignOutAsync();

        context.Response.Headers[HeaderNames.CacheControl] = "no-store";

        return StatusCode(StatusCodes.NoContent);
    }
}
