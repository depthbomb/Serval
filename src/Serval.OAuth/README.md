# Serval.OAuth

Adds OAuth 2.0 client operations and OpenID Connect browser sign-in to Serval.

Register a local session scheme and an OIDC provider:

```csharp
builder.Services.AddServal().AddAuthentication(options => options.DefaultScheme = "Cookies")
    .AddCookie()
    .AddOpenIdConnect("identity", options =>
    {
        options.ClientId = "splash";
        options.ClientSecret = configuration["Identity:ClientSecret"];
        options.ClientAuthentication = OAuthClientAuthentication.ClientSecretBasic;
        options.RedirectUri = new Uri("https://splash.example/auth/callback");
        options.OpenIdConnect!.Issuer = "https://identity.example/tenant";
        options.Scopes.Add("profile");
        options.Scopes.Add("email");
    });
```

In the login action, call `await context.BeginOAuthSignInAsync("identity", "/")`. In the callback action, call
`await context.CompleteOAuthSignInAsync("identity")` and redirect to the returned `ReturnUrl`. These are the same
methods used by ordinary OAuth providers. Catch `OAuthProtocolException` and show a generic login failure.

For account linking, enrollment, or MFA, use `CompleteOAuthCallbackAsync` instead. It consumes the callback and performs
the same protocol validation, then returns before account mapping, principal assignment, or local sign-in:

```csharp
public static async ValueTask<OAuthCallbackResult> VerifyExternalIdentityAsync(RequestContext context)
{
    var callback = await context.CompleteOAuthCallbackAsync("identity");
    var identity = callback.SignInContext.OpenIdConnect
        ?? throw new InvalidOperationException("This endpoint requires OIDC.");
    // Use the validated issuer and subject for account lookup or a pending MFA attempt.
    // Keep pending state server-side. Issue the local session only after application checks finish.
    return callback;
}
```

The result includes `SignInContext` (client, tokens, and validated OIDC identity), `ReturnUrl`, and `Properties` from
the pending attempt. Account-linking code can preserve the already authenticated `context.User` while validating the
external account. A sign-in controller can call `context.SignInAsync(localPrincipal, properties: callback.Properties)`
after its account checks. For MFA, persist only the required pending identity in short-lived browser-bound state and
create the session after MFA succeeds; do not retain the request context across requests.

`CreatePrincipal` is optional when handling completion in application code. Ordinary OAuth returns provider tokens,
which require provider-specific identity verification before local sign-in. The combined `CompleteOAuthSignInAsync`
helper still requires `CreatePrincipal` for ordinary OAuth and uses the validated OIDC principal by default for OIDC.
Both completion methods consume the same single-use state, so call exactly one for each callback.

Discovery uses the configured issuer's `/.well-known/openid-configuration` endpoint, including its path. The issuer
must match exactly, including any trailing slash. Set `MetadataAddress` for a different discovery location. Endpoints
come from discovery; do not also configure OAuth endpoint properties. HTTPS is required, with the existing explicit
loopback HTTP option available for development. Supplied backchannel clients must disable redirects. Discovery and
JWKS requests share the OAuth response-size and timeout limits.

Sign-in always uses the authorization-code flow, S256 PKCE, a random nonce, single-use state, and browser correlation.
The `openid` scope is added automatically. Before creating a session, Serval validates the ID-token signature, issuer,
subject, audience, authorized party, timestamps, and nonce. It also checks `at_hash` and `c_hash` when present. Additional
audiences require an explicit `AdditionalAudiences` entry; the client ID must still be an audience, and multiple audiences
require `azp` to identify this client. `MaxAge` sends `max_age` and enforces `auth_time`.

RS256 is the default signing algorithm. `SigningAlgorithms` can explicitly allow RS384/512, PS256/384/512, or
ES256/384/512 when negotiated with the provider. Microsoft.IdentityModel.JsonWebTokens handles JWT signature
verification. Discovery and keys are cached for twelve hours by default. A signature/key failure triggers one refresh
and retry, with a thirty-second cooldown shared by requests. Refresh failures fail sign-in without accepting stale
validation results. Unknown signing algorithms, unsigned tokens, HMAC tokens, encrypted tokens, and critical JOSE
extensions are rejected.

Without a mapping callback, the session uses the validated ID-token principal, preserving provider claim names
(`sub`, `name`, and `roles`). For local account lookup or creation, set `CreatePrincipal` and read
`signIn.OpenIdConnect.Claims` or `signIn.OpenIdConnect.Principal`. This callback runs after validation. Identify external
accounts using both issuer and subject. Set `OpenIdConnect.GetClaimsFromUserInfoEndpoint = true` to fetch UserInfo;
its `sub` must match the ID token before it is exposed through `signIn.OpenIdConnect.UserInfo`. UserInfo is not
automatically merged into the principal.

`OAuthTokenResponse.IdToken` contains the raw token. Low-level code exchange and refresh methods return tokens;
they do not independently authenticate a user. ID-token validation is part of browser sign-in completion. Keep all
tokens server-side. Custom `ReadTokenResponse` callbacks must populate `IdToken` for OIDC.

The default cookie-session and pending-state stores are in memory. Replace `IAuthenticationSessionStore` and
`IOAuthStateStore` for persistent or shared storage. Custom state stores must preserve `OAuthState.Nonce` when storing
and consuming OIDC attempts. Existing OAuth registrations do not require a nonce.

This implements OIDC code-flow sign-in with query callbacks. Implicit/hybrid flows, form-post callbacks, request
objects, dynamic client registration, encrypted/signed UserInfo responses, and provider logout protocols are not
implemented. Local sign-out only revokes the local session. The
[OIDC Core](https://openid.net/specs/openid-connect-core-1_0.html) and
[Discovery](https://openid.net/specs/openid-connect-discovery-1_0.html) specifications define the protocol requirements.

The OAuth sample accepts `OAuth__Issuer` to enable this flow, alongside `OAuth__ClientId` and optional client credentials.

See the [main README](https://github.com/depthbomb/serval/blob/master/README.md) for setup and examples.
