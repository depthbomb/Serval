# Serval.OAuth

OAuth 2.0 clients and OpenID Connect sign-in for Serval. Use it to sign users in through an external provider and
create a local session.

## Set up a provider

During host setup, register cookie sessions and an OIDC provider:

```csharp
builder.Services.AddServal().AddAuthentication(options => options.DefaultScheme = "Cookies")
    .AddCookie()
    .AddOpenIdConnect("identity", options =>
    {
        options.ClientId = "my-app";
        options.ClientSecret = builder.Configuration["Identity:ClientSecret"];
        options.ClientAuthentication = OAuthClientAuthentication.ClientSecretBasic;
        options.RedirectUri = new Uri("https://app.example/auth/callback");
        options.OpenIdConnect!.Issuer = "https://identity.example/tenant";
        options.Scopes.Add("profile");
        options.Scopes.Add("email");
    });
```

Await `context.BeginOAuthSignInAsync("identity", "/")` in your login action. In the callback action, await
`context.CompleteOAuthSignInAsync("identity")` and redirect to the returned `ReturnUrl`.
Catch `OAuthProtocolException` and show a generic sign-in failure.

Serval handles discovery, PKCE, state, browser correlation, nonce checks, and ID-token validation.
The `openid` scope is added automatically. The issuer must match the provider exactly, including any trailing slash.
Discovery supplies the OAuth endpoints, so leave those endpoint properties unset.

The [OAuth sample](https://github.com/depthbomb/serval/tree/master/samples/Serval.Sample.OAuth) includes both actions and provider configuration.
Set `OAuth__Issuer`, `OAuth__ClientId`, and any required client credentials to try OIDC.

## Use your own account flow

OIDC uses the validated ID-token principal by default. Set `CreatePrincipal` to look up or create a local account,
using both issuer and subject to identify the external account. For ordinary OAuth sign-in through
`CompleteOAuthSignInAsync`, supply `CreatePrincipal` and verify the identity with the provider.

For account linking, enrollment, or MFA, call `CompleteOAuthCallbackAsync` instead. It validates the callback and
returns `SignInContext`, `ReturnUrl`, and `Properties` without assigning a principal or creating a local session.
Finish your application checks before calling `context.SignInAsync`. Keep pending MFA state short-lived, server-side,
and tied to the browser.

Call only one completion method per callback: both consume the same single-use state.

## Deployment notes

- Use HTTPS. The explicit loopback HTTP option is for local development. Custom backchannel clients must disable redirects.
- Keep tokens server-side. Low-level token exchange and refresh methods do not authenticate a user on their own.
- The default session and pending-state stores are in memory. Replace `IAuthenticationSessionStore` and
  `IOAuthStateStore` for persistence or multiple instances; custom state stores must preserve `OAuthState.Nonce`.
- OIDC supports authorization-code sign-in with query callbacks. Local sign-out revokes the local session;
  provider logout is not implemented.

See [OpenIdConnectOptions](https://github.com/depthbomb/serval/blob/master/src/Serval.OAuth/OpenIdConnectOptions.cs) for signing algorithms,
UserInfo, and other provider settings, or the [quick start](https://github.com/depthbomb/serval/blob/master/README.md#try-it-out) for Serval setup.
