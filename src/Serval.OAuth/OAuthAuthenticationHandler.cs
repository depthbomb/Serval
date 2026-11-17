#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Serval.Authentication;
using Serval.Http.Constants;

namespace Serval.OAuth;

/// <summary>Returns the principal, provider tokens, and local destination after sign-in.</summary>
public sealed class OAuthSignInResult
{
    /// <summary>The application principal passed to the local sign-in handler.</summary>
    public ClaimsPrincipal Principal { get; }

    /// <summary>The provider credentials, which remain available for application-side storage.</summary>
    public OAuthTokenResponse Tokens { get; }

    /// <summary>The validated local destination. Completion does not redirect automatically.</summary>
    public string ReturnUrl { get; }

    /// <summary>Creates a sign-in result with the principal, tokens, and return URL.</summary>
    /// <param name="principal">The authenticated application principal.</param>
    /// <param name="tokens">The provider's token response.</param>
    /// <param name="returnUrl">The validated local return path from the pending attempt.</param>
    public OAuthSignInResult(ClaimsPrincipal principal, OAuthTokenResponse tokens, string returnUrl)
    {
        Principal = principal;
        Tokens    = tokens;
        ReturnUrl = returnUrl;
    }
}

/// <summary>Coordinates browser OAuth sign-in and delegates the resulting session to a local scheme.</summary>
public sealed class OAuthAuthenticationHandler : AuthenticationHandler
{
    private readonly OAuthClient      _client;
    private readonly OAuthOptions     _options;
    private readonly IOAuthStateStore _states;
    private readonly TimeProvider     _time;

    /// <summary>Creates a browser sign-in handler.</summary>
    /// <param name="options">The validated provider and local-session settings.</param>
    /// <param name="client">The provider's OAuth client.</param>
    /// <param name="states">The expiring, single-use state store.</param>
    /// <param name="timeProvider">The clock used for sign-in attempt expiration.</param>
    public OAuthAuthenticationHandler(OAuthOptions options, OAuthClient client, IOAuthStateStore states, TimeProvider timeProvider)
    {
        options.Validate();
        _options = options;
        _client  = client;
        _states  = states;
        _time    = timeProvider;
    }

    /// <inheritdoc />
    /// <remarks>Always returns Missing. The separate local session scheme authenticates normal requests.</remarks>
    public override ValueTask<AuthenticationResult> AuthenticateAsync(RequestContext context, string scheme)
        => ValueTask.FromResult(AuthenticationResult.Missing());

    /// <inheritdoc />
    /// <remarks>Starts browser sign-in with / as the local return destination.</remarks>
    public override ValueTask ChallengeAsync(RequestContext context, string scheme, AuthenticationProperties properties)
        => BeginAsync(context, scheme, "/", properties);

    /// <summary>Starts an authorization-code flow with browser correlation and PKCE.</summary>
    /// <param name="context">The current request and its scoped services.</param>
    /// <param name="scheme">The registered OAuth provider scheme.</param>
    /// <param name="returnUrl">The local return path starting with a single slash; defaults to /.</param>
    /// <param name="properties">Optional properties for the eventual local session.</param>
    /// <param name="parameters">Additional provider-specific authorization parameters.</param>
    /// <returns>An operation that completes when the authorization redirect is sent.</returns>
    /// <remarks>
    ///     Stores state and a browser-binding cookie, sets private response headers, and redirects with status 302. Must
    ///     run before response startup.
    /// </remarks>
    /// <exception cref="ArgumentException">The return path or authorization parameters are invalid.</exception>
    /// <exception cref="InvalidOperationException">
    ///     The response has started or a secure correlation cookie is requested over HTTP.
    /// </exception>
    public async ValueTask BeginAsync(RequestContext                       context,
                                      string                               scheme,
                                      string                               returnUrl  = "/",
                                      AuthenticationProperties?            properties = null,
                                      IReadOnlyDictionary<string, string>? parameters = null)
    {
        EnsureWritable(context);
        ValidateReturnUrl(returnUrl);

        if (_options.CorrelationCookie.Secure && context.Request.Url.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("Secure OAuth correlation cookies require an HTTPS request.");
        }

        string? nonce = null;
        OAuthAuthorizationRequest authorization;
        if (_client.OpenIdConnect is { } provider)
        {
            var configuration = await provider.GetAsync(context.RequestAborted);
            nonce = OAuthProtocol.RandomToken();
            authorization = _client.CreateOidcAuthorizationRequest(configuration.AuthorizationEndpoint, _options.RedirectUri, nonce, parameters);
        }
        else
        {
            authorization = _client.CreateAuthorizationRequest(_options.RedirectUri, parameters: parameters);
        }
        var correlation   = OAuthProtocol.RandomToken();
        var expiry        = _time.GetUtcNow() + _options.StateLifetime;
        await _states.StoreAsync(Hash(authorization.State), new OAuthState
        {
            Nonce           = nonce,
            Scheme          = scheme,
            CorrelationHash = Hash(correlation),
            CodeVerifier    = authorization.CodeVerifier,
            RedirectUri     = _options.RedirectUri,
            ReturnUrl       = returnUrl,
            ExpiresUtc      = expiry,
            Properties      = properties ?? new AuthenticationProperties(),
        }, context.RequestAborted);
        context.Response.Cookies.Set(_options.CorrelationCookiePrefix + authorization.State, correlation, _options.CorrelationCookie with
        {
            MaxAge = _options.StateLifetime,
            Expires = expiry
        });

        SetPrivateResponse(context);

        await context.Response.RedirectAsync(authorization.AuthorizationUri.AbsoluteUri, StatusCodes.Found, context.RequestAborted);
    }

    /// <summary>Validates the browser callback and creates a local authenticated session.</summary>
    /// <param name="context">The current request and its scoped services.</param>
    /// <param name="scheme">The registered OAuth provider scheme.</param>
    /// <returns>The signed-in principal, provider tokens, and validated return path.</returns>
    /// <remarks>
    ///     Requires a GET to the configured callback URI. Consumes matching state once, validates the optional issuer,
    ///     exchanges the code, maps the principal, and signs in through the local scheme. The caller decides how to
    ///     redirect afterward.
    /// </remarks>
    /// <exception cref="OAuthProtocolException">
    ///     Callback validation, token exchange, or principal creation fails.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    ///     The response has started or the local session scheme is unavailable.
    /// </exception>
    public async ValueTask<OAuthSignInResult> CompleteAsync(RequestContext context, string scheme)
    {
        if (_options.CreatePrincipal is null && _options.OpenIdConnect is null)
        {
            throw new InvalidOperationException("Configure CreatePrincipal or map the result of CompleteCallbackAsync in application code.");
        }

        var callback = await CompleteCallbackAsync(context, scheme);
        var principal = _options.CreatePrincipal is {} createPrincipal
            ? await createPrincipal(callback.SignInContext)
            : callback.SignInContext.OpenIdConnect?.Principal ?? throw new InvalidOperationException("Configure CreatePrincipal or map the result of CompleteCallbackAsync in application code.");
        context.RequestAborted.ThrowIfCancellationRequested();
        if (principal is null || !principal.Identities.Any(identity => identity.IsAuthenticated))
        {
            throw new OAuthProtocolException("invalid_identity");
        }

        await context.SignInAsync(principal, _options.SignInScheme, callback.Properties);
        return new OAuthSignInResult(principal, callback.SignInContext.Tokens, callback.ReturnUrl);
    }

    /// <summary>Consumes and validates a browser callback without mapping an account or creating a local session.</summary>
    /// <remarks>Includes all correlation, PKCE, issuer, and OIDC checks. Plain OAuth tokens alone do not establish an identity.</remarks>
    public async ValueTask<OAuthCallbackResult> CompleteCallbackAsync(RequestContext context, string scheme)
    {
        EnsureWritable(context);
        SetPrivateResponse(context);

        if (context.Request.Method != HttpMethods.Get || !string.Equals(context.Request.Url.GetLeftPart(UriPartial.Path), _options.RedirectUri.AbsoluteUri, StringComparison.Ordinal))
        {
            throw new OAuthProtocolException("invalid_callback");
        }

        var stateToken = Query(context, "state");
        if (!ValidToken(stateToken))
        {
            throw new OAuthProtocolException("invalid_state");
        }

        var cookieName  = _options.CorrelationCookiePrefix + stateToken;
        var correlation = context.Request.Cookies.GetValueOrDefault(cookieName);
        if (!ValidToken(correlation))
        {
            throw new OAuthProtocolException("invalid_correlation");
        }

        var state = await _states.TakeAsync(Hash(stateToken!), scheme, Hash(correlation!), context.RequestAborted);
        if (state is null || state.ExpiresUtc <= _time.GetUtcNow() || state.RedirectUri != _options.RedirectUri)
        {
            throw new OAuthProtocolException("invalid_state");
        }

        context.Response.Cookies.Delete(cookieName, _options.CorrelationCookie);
        var issuer = Query(context, "iss");
        if (_options.ExpectedIssuer is not null && !string.Equals(issuer, _options.ExpectedIssuer, StringComparison.Ordinal))
        {
            throw new OAuthProtocolException("invalid_issuer");
        }

        if (_client.OpenIdConnect is { } oidcProvider)
        {
            var configuration = await oidcProvider.GetAsync(context.RequestAborted);
            if ((issuer is not null || configuration.RequiresCallbackIssuer) && issuer != _options.OpenIdConnect!.Issuer)
            {
                throw new OAuthProtocolException("invalid_issuer");
            }
        }

        var error = Query(context, "error");
        var code  = Query(context, "code");
        if (error is not null)
        {
            throw new OAuthProtocolException(code is null ? error : "invalid_response");
        }

        if (string.IsNullOrWhiteSpace(code))
        {
            throw new OAuthProtocolException("missing_code");
        }

        var tokens = await _client.ExchangeCodeAsync(code, state.RedirectUri, state.CodeVerifier, context.RequestAborted);
        OpenIdConnectIdentity? identity = null;
        if (_options.OpenIdConnect is not null)
        {
            identity = await new OpenIdConnectValidator(_client, _options, _time).ValidateAsync(tokens, state, code, scheme, context.RequestAborted);
        }

        var signInContext = new OAuthSignInContext(context, scheme, _client, tokens) { OpenIdConnect = identity };
        context.RequestAborted.ThrowIfCancellationRequested();
        return new OAuthCallbackResult(signInContext, state.ReturnUrl, state.Properties);
    }

    private static string? Query(RequestContext context, string name)
    {
        if (!context.Request.Query.TryGetValue(name, out var values))
        {
            return null;
        }

        if (values.Length != 1 || values[0].Length > 8192)
        {
            throw new OAuthProtocolException("invalid_response");
        }

        return values[0];
    }

    private static void ValidateReturnUrl(string url)
    {
        // Check the decoded form too, so an escaped slash or backslash can't turn a local redirect into another host.
        if (!IsLocalPath(url) || !IsLocalPath(Uri.UnescapeDataString(url)))
        {
            throw new ArgumentException("The return URL must be a local path without control characters or backslashes.", nameof(url));
        }
    }

    private static bool IsLocalPath(string url)
        => !string.IsNullOrEmpty(url) && url[0] == '/' && (url.Length == 1 || url[1] != '/') && !url.Any(character => char.IsControl(character) || character == '\\');

    private static void SetPrivateResponse(RequestContext context)
    {
        context.Response.Headers[HeaderNames.CacheControl]   = CacheControlDirectives.NoStore;
        context.Response.Headers[HeaderNames.Pragma]         = CacheControlDirectives.NoCache;
        context.Response.Headers[HeaderNames.ReferrerPolicy] = "no-referrer";
    }

    private static void EnsureWritable(RequestContext context)
    {
        if (context.Response.HasStarted)
        {
            throw new InvalidOperationException("OAuth sign-in must run before the response starts.");
        }
    }

    private static bool ValidToken(string? token) => token is { Length: 43 } && token.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
