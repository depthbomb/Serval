#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.IdentityModel.Tokens;
using System.Text.Json;

namespace Serval.OAuth;

internal sealed class OpenIdConnectConfiguration
{
    internal required Uri            AuthorizationEndpoint   { get; init; }
    internal required Uri            TokenEndpoint           { get; init; }
    internal required Uri            JwksUri                 { get; init; }
    internal          Uri?           UserInformationEndpoint { get; init; }
    internal          Uri?           RevocationEndpoint      { get; init; }
    internal required string[]       Algorithms              { get; init; }
    internal required JsonWebKey[]   Keys                    { get; init; }
    internal          bool           RequiresCallbackIssuer  { get; init; }
    internal required DateTimeOffset ExpiresUtc              { get; init; }
}

internal sealed class OpenIdConnectProvider : IDisposable
{
    private OpenIdConnectConfiguration? _configuration;
    private DateTimeOffset              _lastForcedRefresh = DateTimeOffset.MinValue;

    private readonly OAuthClient          _client;
    private readonly OAuthOptions         _oauth;
    private readonly OpenIdConnectOptions _options;
    private readonly TimeProvider         _time;
    private readonly SemaphoreSlim        _gate = new(1, 1);

    internal OpenIdConnectProvider(OAuthClient client, OAuthOptions oauth, TimeProvider time)
    {
        _client  = client;
        _oauth   = oauth;
        _options = oauth.OpenIdConnect!;
        _time    = time;
    }

    public void Dispose() => _gate.Dispose();

    internal async ValueTask<OpenIdConnectConfiguration> GetAsync(CancellationToken ct, OpenIdConnectConfiguration? refresh = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_oauth.BackchannelTimeout);
        ct = timeout.Token;
        await _gate.WaitAsync(ct);
        try
        {
            var now = _time.GetUtcNow();
            if (_configuration is not null && _configuration.ExpiresUtc > now)
            {
                if (refresh is null || !ReferenceEquals(refresh, _configuration) || now - _lastForcedRefresh < _options.RefreshCooldown)
                {
                    return _configuration;
                }
            }

            if (refresh is not null)
            {
                // Count failed attempts too, so a bad key ID cannot trigger unbounded refresh traffic.
                if (now - _lastForcedRefresh < _options.RefreshCooldown)
                {
                    throw new OAuthProtocolException("oidc_refresh_throttled");
                }

                _lastForcedRefresh = now;
            }

            var address  = _options.MetadataAddress ?? new Uri(_options.Issuer.TrimEnd('/') + "/.well-known/openid-configuration");
            var metadata = await _client.GetDocumentAsync(address, "oidc_discovery", ct);
            if (OAuthProtocol.String(metadata, "issuer") != _options.Issuer)
            {
                throw new OAuthProtocolException("invalid_issuer");
            }

            var authorization = Endpoint(metadata, "authorization_endpoint", true)!;
            var token         = Endpoint(metadata, "token_endpoint", true)!;
            var jwks          = Endpoint(metadata, "jwks_uri", true)!;
            var userInfo      = Endpoint(metadata, "userinfo_endpoint", _options.GetClaimsFromUserInfoEndpoint);
            var revocation    = Endpoint(metadata, "revocation_endpoint", false);
            var algorithms    = Strings(metadata, "id_token_signing_alg_values_supported", true);
            var endpointParameters = authorization.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).Select(part =>
                new KeyValuePair<string, string>(Uri.UnescapeDataString(part.Split('=', 2)[0].Replace('+', ' ')), ""));
            try
            {
                OpenIdConnectOptions.ValidateParameters(endpointParameters);
            }
            catch (ArgumentException)
            {
                throw new OAuthProtocolException("invalid_discovery");
            }

            if (!Strings(metadata, "response_types_supported", true).Contains("code", StringComparer.Ordinal) ||
                !algorithms.Intersect(_options.SigningAlgorithms, StringComparer.Ordinal).Any())
            {
                throw new OAuthProtocolException("unsupported_oidc_provider");
            }

            if (metadata.TryGetProperty("code_challenge_methods_supported", out _) &&
                !Strings(metadata, "code_challenge_methods_supported", false).Contains("S256", StringComparer.Ordinal))
            {
                throw new OAuthProtocolException("unsupported_pkce_method");
            }

            var requiresIssuer = false;
            if (metadata.TryGetProperty("authorization_response_iss_parameter_supported", out var issuerParameter))
            {
                if (issuerParameter.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    throw new OAuthProtocolException("invalid_discovery");
                }

                requiresIssuer = issuerParameter.GetBoolean();
            }

            var keySet = await _client.GetDocumentAsync(jwks, "oidc_jwks", ct);
            if (!keySet.TryGetProperty("keys", out var keys) || keys.ValueKind != JsonValueKind.Array || keys.GetArrayLength() is 0 or > 100)
            {
                throw new OAuthProtocolException("invalid_jwks");
            }

            var signingKeys = new List<JsonWebKey>();
            foreach (var key in keys.EnumerateArray())
            {
                OAuthProtocol.ValidateObject(key);
                var type = OAuthProtocol.String(key, "kty");
                var use  = OAuthProtocol.String(key, "use");
                var ops  = Strings(key, "key_ops", false);
                if (type is not ("RSA" or "EC") || use is not (null or "sig") ||
                    key.TryGetProperty("key_ops", out _) && !ops.Contains("verify", StringComparer.Ordinal))
                {
                    continue;
                }

                try
                {
                    signingKeys.Add(new JsonWebKey(key.GetRawText()));
                }
                catch (ArgumentException)
                {
                    throw new OAuthProtocolException("invalid_jwks");
                }
            }

            if (signingKeys.Count == 0)
            {
                throw new OAuthProtocolException("invalid_jwks");
            }

            ct.ThrowIfCancellationRequested();

            _configuration = new OpenIdConnectConfiguration
            {
                AuthorizationEndpoint   = authorization,
                TokenEndpoint           = token,
                JwksUri                 = jwks,
                UserInformationEndpoint = userInfo,
                RevocationEndpoint      = revocation,
                Algorithms              = algorithms,
                Keys                    = signingKeys.ToArray(),
                RequiresCallbackIssuer  = requiresIssuer,
                ExpiresUtc              = _time.GetUtcNow() + _options.RefreshInterval,
            };
            return _configuration;
        }
        finally
        {
            _gate.Release();
        }
    }

    private Uri? Endpoint(JsonElement metadata, string name, bool required)
    {
        var value = OAuthProtocol.String(metadata, name);
        if (value is null && !required)
        {
            return null;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            throw new OAuthProtocolException("invalid_discovery");
        }

        try
        {
            _oauth.ValidateUri(uri, name);
        }
        catch (ArgumentException)
        {
            throw new OAuthProtocolException("invalid_discovery");
        }

        return uri;
    }

    private static string[] Strings(JsonElement root, string name, bool required)
    {
        if (!root.TryGetProperty(name, out var array))
        {
            if (required)
            {
                throw new OAuthProtocolException("invalid_discovery");
            }

            return [];
        }

        if (array.ValueKind != JsonValueKind.Array || array.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(item.GetString())))
        {
            throw new OAuthProtocolException("invalid_response");
        }

        return array.EnumerateArray().Select(item => item.GetString()!).ToArray();
    }
}
