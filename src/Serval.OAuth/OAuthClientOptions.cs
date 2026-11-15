#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using System.Security.Claims;
using System.Text.Json;
using Serval.Http;

namespace Serval.OAuth;

/// <summary>Selects how a client identifies itself at token and revocation endpoints.</summary>
public enum OAuthClientAuthentication
{
    /// <summary>Sends client_id without a client secret.</summary>
    None,

    /// <summary>Sends form-encoded client credentials in an HTTP Basic authorization header.</summary>
    ClientSecretBasic,

    /// <summary>Sends client_id and client_secret in the form body.</summary>
    ClientSecretPost,
}

/// <summary>Configures OAuth endpoints, credentials, scopes, and backchannel behavior.</summary>
/// <remarks>
///     Configure before creating the client and keep settings stable while it is in use. Endpoints require HTTPS unless
///     the
///     explicit loopback HTTP exception is enabled.
/// </remarks>
public class OAuthClientOptions
{
    /// <summary>The nonempty client identifier registered with the provider.</summary>
    public string ClientId { get; set; } = "";

    /// <summary>
    ///     The client secret, required for secret-based authentication and absent when ClientAuthentication is None.
    /// </summary>
    public string? ClientSecret { get; set; }

    /// <summary>The optional authorization endpoint, required for browser authorization requests.</summary>
    public Uri? AuthorizationEndpoint { get; set; }

    /// <summary>The required token endpoint used for code exchange, refresh, and client credentials.</summary>
    public Uri TokenEndpoint { get; set; } = null!;

    /// <summary>The optional endpoint used by GetUserInformationAsync.</summary>
    public Uri? UserInformationEndpoint { get; set; }

    /// <summary>The optional endpoint used by RevokeAsync.</summary>
    public Uri? RevocationEndpoint { get; set; }

    /// <summary>The token and revocation authentication method. Defaults to None.</summary>
    public OAuthClientAuthentication ClientAuthentication { get; set; } = OAuthClientAuthentication.None;

    /// <summary>
    ///     Default scope tokens for authorization and client-credentials requests. Duplicates are removed with ordinal
    ///     comparison.
    /// </summary>
    public IList<string> Scopes { get; } = new List<string>();

    /// <summary>
    ///     Additional provider-specific authorization parameters. Reserved or duplicate parameter names are rejected.
    /// </summary>
    public IDictionary<string, string> AuthorizationParameters { get; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    ///     Additional provider-specific token parameters. Reserved or duplicate parameter names are rejected.
    /// </summary>
    public IDictionary<string, string> TokenParameters { get; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Whether HTTP endpoints are allowed on loopback hosts for development. Defaults to false.</summary>
    public bool AllowLoopbackHttp { get; set; }

    /// <summary>The positive maximum backchannel response size in bytes. Defaults to 1 MiB.</summary>
    public int MaximumResponseBytes { get; set; } = 1024 * 1024;

    // Includes request preparation, HTTP I/O, and custom token response handling.
    /// <summary>
    ///     The positive deadline covering request preparation, HTTP I/O, and custom token parsing. Defaults to 30 seconds.
    /// </summary>
    public TimeSpan BackchannelTimeout { get; set; } = TimeSpan.FromSeconds(30);

    // A supplied client remains owned by the application. Disable redirects on its handler.
    /// <summary>
    ///     An optional application-owned HttpClient. Its handler must disable automatic redirects; the OAuth client does
    ///     not dispose it.
    /// </summary>
    public HttpClient? Backchannel { get; set; }

    /// <summary>
    ///     An optional callback to customize an outgoing request before its URI is validated and the request is sent.
    /// </summary>
    public Func<OAuthRequestContext, ValueTask>? PrepareRequest { get; set; }

    /// <summary>
    ///     An optional provider-specific token parser. Receives a detached JSON object and the backchannel cancellation
    ///     token.
    /// </summary>
    public Func<JsonElement, CancellationToken, ValueTask<OAuthTokenResponse>>? ReadTokenResponse { get; set; }

    internal virtual void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ClientId);

        ValidateUri(TokenEndpoint, nameof(TokenEndpoint));

        foreach (var endpoint in new[] { AuthorizationEndpoint, UserInformationEndpoint, RevocationEndpoint })
        {
            if (endpoint is not null)
            {
                ValidateUri(endpoint, "endpoint");
            }
        }

        if (!Enum.IsDefined(ClientAuthentication))
        {
            throw new ArgumentOutOfRangeException(nameof(ClientAuthentication));
        }

        if (ClientAuthentication != OAuthClientAuthentication.None && string.IsNullOrEmpty(ClientSecret))
        {
            throw new ArgumentException("The selected client authentication method requires a secret.");
        }

        if (ClientAuthentication == OAuthClientAuthentication.None && ClientSecret is not null)
        {
            throw new ArgumentException("Select a client authentication method when configuring a secret.");
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumResponseBytes);
        if (BackchannelTimeout <= TimeSpan.Zero || BackchannelTimeout.TotalMilliseconds > uint.MaxValue - 1)
        {
            throw new ArgumentOutOfRangeException(nameof(BackchannelTimeout));
        }

        OAuthProtocol.Scope(Scopes);
    }

    internal void ValidateUri(Uri? uri, string name)
    {
        if (
            uri is null              ||
            !uri.IsAbsoluteUri       ||
            uri.UserInfo.Length != 0 ||
            uri.Fragment.Length != 0 ||
            uri.Scheme != Uri.UriSchemeHttps &&
            !(AllowLoopbackHttp && uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback))
        {
            throw new ArgumentException("OAuth endpoints require HTTPS, with an explicit exception available for loopback development.", name);
        }
    }
}

/// <summary>Adds browser sign-in, correlation, and local-session settings to an OAuth client.</summary>
public sealed class OAuthOptions : OAuthClientOptions
{
    /// <summary>
    ///     The absolute callback URI. Must have no query, user information, or fragment, and must satisfy the endpoint
    ///     transport policy.
    /// </summary>
    public Uri RedirectUri { get; set; } = null!;

    /// <summary>
    ///     The separate local session scheme that receives the authenticated principal. Defaults to Cookies.
    /// </summary>
    public string SignInScheme { get; set; } = "Cookies";

    /// <summary>The prefix for per-attempt correlation cookies. Defaults to __Host-Serval.OAuth.</summary>
    public string CorrelationCookiePrefix { get; set; } = "__Host-Serval.OAuth.";

    /// <summary>Flags, path, and domain for the browser-binding cookie. Expiration comes from StateLifetime.</summary>
    public CookieOptions CorrelationCookie { get; set; } = new();

    /// <summary>The positive sign-in attempt lifetime, at most one hour. Defaults to ten minutes.</summary>
    public TimeSpan StateLifetime { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>The expected callback iss value, compared exactly when configured.</summary>
    public string? ExpectedIssuer { get; set; }

    /// <summary>
    ///     The required callback that maps provider tokens or user information to a principal with an authenticated
    ///     identity.
    /// </summary>
    public Func<OAuthSignInContext, ValueTask<ClaimsPrincipal>>? CreatePrincipal { get; set; }

    internal override void Validate()
    {
        base.Validate();

        ValidateUri(AuthorizationEndpoint, nameof(AuthorizationEndpoint));
        ValidateUri(RedirectUri, nameof(RedirectUri));

        ArgumentException.ThrowIfNullOrWhiteSpace(SignInScheme);
        ArgumentNullException.ThrowIfNull(CreatePrincipal);

        if (RedirectUri.Query.Length != 0)
        {
            throw new ArgumentException("The browser callback URI must not contain a query.", nameof(RedirectUri));
        }

        if (StateLifetime <= TimeSpan.Zero || StateLifetime > TimeSpan.FromHours(1))
        {
            throw new ArgumentOutOfRangeException(nameof(StateLifetime));
        }

        ArgumentNullException.ThrowIfNull(CorrelationCookie);

        if (CorrelationCookie.MaxAge is not null || CorrelationCookie.Expires is not null)
        {
            throw new ArgumentException("Use StateLifetime to configure correlation cookie expiration.");
        }

        if (CorrelationCookie.Secure && RedirectUri.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException("Secure correlation cookies require an HTTPS callback.");
        }

        new ResponseCookies(new Headers()).Set(CorrelationCookiePrefix + "validation", "validation", CorrelationCookie);
    }
}

/// <summary>Provides an outgoing backchannel request to the customization callback.</summary>
public sealed class OAuthRequestContext
{
    /// <summary>The outgoing request, owned and disposed by the OAuth client.</summary>
    public HttpRequestMessage Request { get; }

    /// <summary>The OAuth operation being performed.</summary>
    public string Operation { get; }

    /// <summary>The linked caller cancellation and backchannel deadline token.</summary>
    public CancellationToken CancellationToken { get; }

    /// <summary>Creates a context for an outgoing OAuth request.</summary>
    /// <param name="request">The request about to be sent.</param>
    /// <param name="operation">
    ///     The operation name: authorization_code, refresh_token, client_credentials, revocation, or user_information.
    /// </param>
    /// <param name="cancellationToken">The caller token linked to the backchannel deadline.</param>
    public OAuthRequestContext(HttpRequestMessage request, string operation, CancellationToken cancellationToken)
    {
        Request           = request;
        Operation         = operation;
        CancellationToken = cancellationToken;
    }
}

/// <summary>Provides the callback with provider tokens and the current request.</summary>
public sealed class OAuthSignInContext
{
    /// <summary>The browser callback request and its scoped services.</summary>
    public RequestContext Request { get; }

    /// <summary>The provider's authentication scheme.</summary>
    public string Scheme { get; }

    /// <summary>The OAuth client for follow-up user-information or token operations.</summary>
    public OAuthClient Client { get; }

    /// <summary>The received credentials. Application code decides whether and where to store them.</summary>
    public OAuthTokenResponse Tokens { get; }

    /// <summary>Creates a sign-in context for the current request and exchanged tokens.</summary>
    /// <param name="request">The current request and its scoped services.</param>
    /// <param name="scheme">The OAuth provider's registered scheme.</param>
    /// <param name="client">The client available for follow-up provider requests.</param>
    /// <param name="tokens">The tokens returned by the code exchange.</param>
    public OAuthSignInContext(RequestContext request, string scheme, OAuthClient client, OAuthTokenResponse tokens)
    {
        Request = request;
        Scheme  = scheme;
        Client  = client;
        Tokens  = tokens;
    }
}
