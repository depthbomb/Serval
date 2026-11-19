#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Serval.Http.Constants;

namespace Serval.OAuth;

/// <summary>Performs OAuth authorization-code, refresh, client-credentials, and token operations.</summary>
/// <remarks>
///     Uses bounded JSON responses and a deadline for each backchannel operation. Retain state and PKCE verifiers securely
///     when using the low-level authorization flow.
/// </remarks>
public sealed class OAuthClient : IDisposable
{
    internal OpenIdConnectProvider? OpenIdConnect { get; }

    private readonly HttpClient         _http;
    private readonly OAuthClientOptions _options;
    private readonly bool               _ownsHttp;

    /// <summary>Validates options and creates or uses a backchannel HTTP client.</summary>
    /// <param name="options">The provider endpoints, credentials, and request settings.</param>
    /// <remarks>A default client disables redirects and cookies. A supplied client remains application-owned.</remarks>
    public OAuthClient(OAuthClientOptions options) : this(options, TimeProvider.System) {}

    internal OAuthClient(OAuthClientOptions options, TimeProvider time)
    {
        options.Validate();

        _options  = options;
        _ownsHttp = options.Backchannel is null;
        _http = options.Backchannel ?? new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect        = false,
            UseCookies               = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        if (options is OAuthOptions { OpenIdConnect: not null } oidc)
        {
            OpenIdConnect = new OpenIdConnectProvider(this, oidc, time);
        }
    }

    /// <summary>Disposes the HTTP client only when this instance created it.</summary>
    /// <remarks>An application-supplied backchannel remains open.</remarks>
    public void Dispose()
    {
        OpenIdConnect?.Dispose();
        if (_ownsHttp)
        {
            _http.Dispose();
        }
    }

    /// <summary>Builds an authorization-code request with a fresh S256 PKCE verifier.</summary>
    /// <param name="redirectUri">The registered absolute callback URI.</param>
    /// <param name="state">The state value, or null to generate a random value.</param>
    /// <param name="scopes">Requested scopes, or null to use the configured defaults.</param>
    /// <param name="parameters">
    ///     Optional extra authorization parameters that do not override reserved or existing fields.
    /// </param>
    /// <returns>The browser URL together with the state and secret verifier.</returns>
    /// <remarks>This low-level method does not store state or set browser-correlation cookies.</remarks>
    /// <exception cref="InvalidOperationException">No authorization endpoint is configured.</exception>
    /// <exception cref="ArgumentException">
    ///     The redirect URI, state, scopes, or additional parameters are invalid.
    /// </exception>
    public OAuthAuthorizationRequest CreateAuthorizationRequest(Uri                                  redirectUri,
                                                                string?                              state      = null,
                                                                IEnumerable<string>?                 scopes     = null,
                                                                IReadOnlyDictionary<string, string>? parameters = null)
    {
        var endpoint = _options.AuthorizationEndpoint ?? throw new InvalidOperationException("An authorization endpoint is required.");
        return CreateAuthorizationRequestCore(endpoint, redirectUri, state, scopes, parameters);
    }

    /// <summary>Exchanges an authorization code and PKCE verifier for tokens.</summary>
    /// <param name="code">The nonempty authorization code.</param>
    /// <param name="redirectUri">The same callback URI used for authorization.</param>
    /// <param name="codeVerifier">The original PKCE verifier, from 43 through 128 unreserved ASCII characters.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The validated token response.</returns>
    /// <exception cref="OAuthProtocolException">
    ///     The provider rejects the operation, returns invalid data, redirects, or exceeds the response limit.
    /// </exception>
    /// <exception cref="OperationCanceledException">The caller cancels or the backchannel deadline expires.</exception>
    /// <exception cref="ArgumentException">The code, callback URI, or verifier is invalid.</exception>
    public ValueTask<OAuthTokenResponse> ExchangeCodeAsync(string            code,
                                                           Uri               redirectUri,
                                                           string            codeVerifier,
                                                           CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);

        _options.ValidateUri(redirectUri, nameof(redirectUri));
        _ = OAuthProtocol.Challenge(codeVerifier);
        return TokenAsync("authorization_code", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["grant_type"]    = "authorization_code",
            ["code"]          = code,
            ["redirect_uri"]  = redirectUri.AbsoluteUri,
            ["code_verifier"] = codeVerifier,
        }, cancellationToken);
    }

    /// <summary>Uses a refresh token to request new tokens.</summary>
    /// <param name="refreshToken">The nonempty refresh token.</param>
    /// <param name="scopes">
    ///     Optional scopes; null omits the scope field rather than applying configured defaults.
    /// </param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The provider's new token response.</returns>
    /// <exception cref="OAuthProtocolException">
    ///     The provider rejects the operation, returns invalid data, redirects, or exceeds the response limit.
    /// </exception>
    /// <exception cref="OperationCanceledException">The caller cancels or the backchannel deadline expires.</exception>
    public ValueTask<OAuthTokenResponse> RefreshAsync(string               refreshToken,
                                                      IEnumerable<string>? scopes            = null,
                                                      CancellationToken    cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(refreshToken);

        var form = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["grant_type"]    = "refresh_token",
            ["refresh_token"] = refreshToken,
        };
        AddScope(form, scopes);
        return TokenAsync("refresh_token", form, cancellationToken);
    }

    /// <summary>Requests tokens for the configured client credentials.</summary>
    /// <param name="scopes">Requested scopes, or null for the configured defaults.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The validated token response.</returns>
    /// <exception cref="OAuthProtocolException">
    ///     The provider rejects the operation, returns invalid data, redirects, or exceeds the response limit.
    /// </exception>
    /// <exception cref="OperationCanceledException">The caller cancels or the backchannel deadline expires.</exception>
    public ValueTask<OAuthTokenResponse> ClientCredentialsAsync(IEnumerable<string>? scopes            = null,
                                                                CancellationToken    cancellationToken = default)
    {
        var form = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["grant_type"] = "client_credentials",
        };
        AddScope(form, scopes ?? _options.Scopes);
        return TokenAsync("client_credentials", form, cancellationToken);
    }

    /// <summary>Submits a token to the configured revocation endpoint.</summary>
    /// <param name="token">The nonempty token to revoke.</param>
    /// <param name="tokenTypeHint">An optional token_type_hint, such as access_token or refresh_token.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>An operation that completes when the endpoint returns success.</returns>
    /// <remarks>Successful revocation responses need not contain JSON.</remarks>
    /// <exception cref="OAuthProtocolException">
    ///     The provider rejects the operation, returns invalid data, redirects, or exceeds the response limit.
    /// </exception>
    /// <exception cref="OperationCanceledException">The caller cancels or the backchannel deadline expires.</exception>
    /// <exception cref="InvalidOperationException">No revocation endpoint is configured.</exception>
    public async ValueTask RevokeAsync(string token, string? tokenTypeHint = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        var endpoint = (OpenIdConnect is null ? _options.RevocationEndpoint : (await OpenIdConnect.GetAsync(cancellationToken)).RevocationEndpoint)
                       ?? throw new InvalidOperationException("A revocation endpoint is required.");
        var form = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["token"] = token,
        };
        if (tokenTypeHint is not null)
        {
            form.Add("token_type_hint", tokenTypeHint);
        }

        using var request = FormRequest(endpoint, form);
        using var timeout = CreateTimeout(cancellationToken);
        await SendAsync(request, "revocation", true, timeout.Token);
    }

    /// <summary>Fetches provider user information using a Bearer access token.</summary>
    /// <param name="tokens">The token response containing a Bearer token.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>A detached JSON object that remains valid after the request completes.</returns>
    /// <exception cref="OAuthProtocolException">
    ///     The provider rejects the operation, returns invalid data, redirects, or exceeds the response limit.
    /// </exception>
    /// <exception cref="OperationCanceledException">The caller cancels or the backchannel deadline expires.</exception>
    /// <exception cref="InvalidOperationException">No user-information endpoint is configured.</exception>
    public async ValueTask<JsonElement> GetUserInformationAsync(OAuthTokenResponse tokens,
                                                                CancellationToken  cancellationToken = default)
    {
        var endpoint = (OpenIdConnect is null ? _options.UserInformationEndpoint : (await OpenIdConnect.GetAsync(cancellationToken)).UserInformationEndpoint)
                       ?? throw new InvalidOperationException("A user information endpoint is required.");
        if (!tokens.TokenType.Equals(HttpAuthenticationSchemes.Bearer, StringComparison.OrdinalIgnoreCase))
        {
            throw new OAuthProtocolException("unsupported_token_type");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        using var timeout = CreateTimeout(cancellationToken);
        request.Headers.Authorization = new AuthenticationHeaderValue(HttpAuthenticationSchemes.Bearer, tokens.AccessToken);
        return await SendAsync(request, "user_information", false, timeout.Token);
    }

    internal OAuthAuthorizationRequest CreateOidcAuthorizationRequest(Uri                                  endpoint,
                                                                      Uri                                  redirectUri,
                                                                      string                               nonce,
                                                                      IReadOnlyDictionary<string, string>? parameters)
    {
        OpenIdConnectOptions.ValidateParameters(parameters);
        OpenIdConnectOptions.ValidateParameters(_options.AuthorizationParameters);
        return CreateAuthorizationRequestCore(endpoint, redirectUri, null, _options.Scopes.Prepend("openid"), parameters, nonce);
    }

    internal async ValueTask<JsonElement> GetDocumentAsync(Uri address, string operation, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, address);
        using var timeout = CreateTimeout(ct);
        return await SendAsync(request, operation, false, timeout.Token);
    }

    private OAuthAuthorizationRequest CreateAuthorizationRequestCore(Uri                                  endpoint,
                                                                     Uri                                  redirectUri,
                                                                     string?                              state,
                                                                     IEnumerable<string>?                 scopes,
                                                                     IReadOnlyDictionary<string, string>? parameters,
                                                                     string?                              nonce = null)
    {
        _options.ValidateUri(redirectUri, nameof(redirectUri));
        state ??= OAuthProtocol.RandomToken();

        ArgumentException.ThrowIfNullOrWhiteSpace(state);

        var verifier = OAuthProtocol.RandomToken();
        var query = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["client_id"]             = _options.ClientId,
            ["redirect_uri"]          = redirectUri.AbsoluteUri,
            ["response_type"]         = "code",
            ["state"]                 = state,
            ["code_challenge"]        = OAuthProtocol.Challenge(verifier),
            ["code_challenge_method"] = "S256",
        };
        var scope = OAuthProtocol.Scope(scopes ?? _options.Scopes);
        if (scope.Length != 0)
        {
            query.Add("scope", scope);
        }

        if (nonce is not null)
        {
            query.Add("nonce", nonce);
            var oidc = ((OAuthOptions)_options).OpenIdConnect!;
            if (oidc.MaxAge is {} maxAge)
            {
                query.Add("max_age", ((long)maxAge.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        OAuthProtocol.AddParameters(query, _options.AuthorizationParameters);
        if (parameters is not null)
        {
            OAuthProtocol.AddParameters(query, parameters);
        }

        return new OAuthAuthorizationRequest(OAuthProtocol.AuthorizationUri(endpoint, query), state, verifier);
    }

    private async ValueTask<OAuthTokenResponse> TokenAsync(string                     operation,
                                                           Dictionary<string, string> form,
                                                           CancellationToken          cancellationToken)
    {
        OAuthProtocol.AddParameters(form, _options.TokenParameters);
        using var timeout  = CreateTimeout(cancellationToken);
        var       endpoint = OpenIdConnect is null ? _options.TokenEndpoint : (await OpenIdConnect.GetAsync(timeout.Token)).TokenEndpoint;
        using var request  = FormRequest(endpoint, form);
        var       root     = await SendAsync(request, operation, false, timeout.Token);

        timeout.Token.ThrowIfCancellationRequested();

        var tokens = _options.ReadTokenResponse is {} readTokens ? await readTokens(root, timeout.Token) : ReadTokens(root);
        // A custom parser may ignore cancellation. Don't accept a result that arrived after the deadline.
        timeout.Token.ThrowIfCancellationRequested();
        if (tokens is null                                ||
            string.IsNullOrWhiteSpace(tokens.AccessToken) ||
            string.IsNullOrWhiteSpace(tokens.TokenType)   ||
            tokens.ExpiresIn < TimeSpan.Zero              ||
            tokens.AccessToken.Any(char.IsControl)        || tokens.TokenType.Any(char.IsControl))
        {
            throw new OAuthProtocolException("invalid_token_response");
        }

        return tokens;
    }

    private HttpRequestMessage FormRequest(Uri endpoint, Dictionary<string, string> form)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        switch (_options.ClientAuthentication)
        {
            case OAuthClientAuthentication.None:
                form.Add("client_id", _options.ClientId);
                break;
            case OAuthClientAuthentication.ClientSecretPost:
                form.Add("client_id", _options.ClientId);
                form.Add("client_secret", _options.ClientSecret!);
                break;
            case OAuthClientAuthentication.ClientSecretBasic:
                var credentials = FormEncode(_options.ClientId) + ":" + FormEncode(_options.ClientSecret!);
                request.Headers.Authorization = new AuthenticationHeaderValue(HttpAuthenticationSchemes.Basic, Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials)));
                break;
        }

        request.Content = new FormUrlEncodedContent(form);
        return request;
    }

    private async ValueTask<JsonElement> SendAsync(HttpRequestMessage request,
                                                   string             operation,
                                                   bool               allowEmpty,
                                                   CancellationToken  cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(MimeTypes.Json));
        if (_options.PrepareRequest is not null)
        {
            await _options.PrepareRequest(new OAuthRequestContext(request, operation, cancellationToken));
        }

        cancellationToken.ThrowIfCancellationRequested();

        // The preparation hook can replace the destination, so validate its final URI before sending credentials.
        _options.ValidateUri(request.RequestUri, nameof(request.RequestUri));
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if ((int)response.StatusCode is >= 300 and < 400)
        {
            throw new OAuthProtocolException("unexpected_redirect", (int)response.StatusCode);
        }

        if (response.Content.Headers.ContentLength > _options.MaximumResponseBytes)
        {
            throw new OAuthProtocolException("response_too_large", (int)response.StatusCode);
        }

        await using var stream  = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var       content = new MemoryStream();
        var             buffer  = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            if (content.Length + read > _options.MaximumResponseBytes)
            {
                throw new OAuthProtocolException("response_too_large", (int)response.StatusCode);
            }

            content.Write(buffer, 0, read);
        }

        if (allowEmpty && response.IsSuccessStatusCode)
        {
            return default;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(content.GetBuffer().AsMemory(0, (int)content.Length), new JsonDocumentOptions
            {
                MaxDepth = 32,
            });
        }
        catch (JsonException)
        {
            throw new OAuthProtocolException("invalid_response", (int)response.StatusCode);
        }

        using (document)
        {
            var root = document.RootElement;
            OAuthProtocol.ValidateObject(root);

            var error = OAuthProtocol.String(root, "error");
            if (error is not null || !response.IsSuccessStatusCode)
            {
                throw new OAuthProtocolException(error ?? "http_error", (int)response.StatusCode);
            }

            // Callers keep this element after the response and JsonDocument have been disposed.
            return root.Clone();
        }
    }

    private CancellationTokenSource CreateTimeout(CancellationToken cancellationToken)
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.BackchannelTimeout);
        return timeout;
    }

    private static OAuthTokenResponse ReadTokens(JsonElement root)
    {
        TimeSpan? expires = null;
        if (root.TryGetProperty("expires_in", out var duration))
        {
            if (duration.ValueKind != JsonValueKind.Number || !duration.TryGetInt64(out var seconds) || seconds < 0 || seconds > TimeSpan.MaxValue.Ticks / TimeSpan.TicksPerSecond)
            {
                throw new OAuthProtocolException("invalid_token_response");
            }

            expires = TimeSpan.FromTicks(seconds * TimeSpan.TicksPerSecond);
        }

        return new OAuthTokenResponse
        {
            AccessToken    = OAuthProtocol.String(root, "access_token") ?? "",
            TokenType      = OAuthProtocol.String(root, "token_type")   ?? "",
            RefreshToken   = OAuthProtocol.String(root, "refresh_token"),
            IdToken        = OAuthProtocol.String(root, "id_token"),
            ExpiresIn      = expires,
            Scope          = OAuthProtocol.String(root, "scope"),
            AdditionalData = root.Clone(),
        };
    }

    private static void AddScope(Dictionary<string, string> form, IEnumerable<string>? scopes)
    {
        if (scopes is not null)
        {
            var scope = OAuthProtocol.Scope(scopes);
            if (scope.Length != 0)
            {
                form.Add("scope", scope);
            }
        }
    }

    private static string FormEncode(string value) => Uri.EscapeDataString(value).Replace("%20", "+", StringComparison.Ordinal);
}
