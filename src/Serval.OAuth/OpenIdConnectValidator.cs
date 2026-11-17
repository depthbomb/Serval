#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Serval.OAuth;

internal sealed class OpenIdConnectValidator
{
    private readonly OAuthClient          _client;
    private readonly OAuthOptions         _oauth;
    private readonly OpenIdConnectOptions _options;
    private readonly TimeProvider         _time;

    internal OpenIdConnectValidator(OAuthClient client, OAuthOptions oauth, TimeProvider time)
    {
        _client  = client;
        _oauth   = oauth;
        _options = oauth.OpenIdConnect!;
        _time    = time;
    }

    internal async ValueTask<OpenIdConnectIdentity> ValidateAsync(OAuthTokenResponse tokens,
                                                                  OAuthState         state,
                                                                  string             code,
                                                                  string             scheme,
                                                                  CancellationToken  ct)
    {
        ct.ThrowIfCancellationRequested();

        if (string.IsNullOrEmpty(tokens.IdToken)               ||
            tokens.IdToken.Length > _options.MaximumTokenBytes ||
            string.IsNullOrEmpty(state.Nonce)                  ||
            !tokens.TokenType.Equals("Bearer", StringComparison.OrdinalIgnoreCase))
        {
            throw new OAuthProtocolException("invalid_id_token");
        }

        var parts = tokens.IdToken.Split('.');
        if (parts.Length != 3 || parts.Any(part => part.Length == 0 || part.Any(value => !char.IsAsciiLetterOrDigit(value) && value is not ('-' or '_'))))
        {
            throw new OAuthProtocolException("invalid_id_token");
        }

        var header = Decode(parts[0]);
        var claims = Decode(parts[1]);
        var alg    = OAuthProtocol.String(header, "alg");
        var kid    = OAuthProtocol.String(header, "kid");
        if (alg is null                               ||
            !_options.SigningAlgorithms.Contains(alg) ||
            header.TryGetProperty("crit", out _)      ||
            header.TryGetProperty("b64", out _))
        {
            throw new OAuthProtocolException("unsupported_id_token_algorithm");
        }

        ValidateClaims(claims, state.Nonce);

        var provider      = _client.OpenIdConnect!;
        var configuration = await provider.GetAsync(ct);
        var result        = await VerifyAsync(tokens.IdToken, alg, kid, configuration, scheme);
        if (result is { IsValid: false, Exception: SecurityTokenSignatureKeyNotFoundException or SecurityTokenInvalidSignatureException })
        {
            configuration = await provider.GetAsync(ct, configuration);
            result        = await VerifyAsync(tokens.IdToken, alg, kid, configuration, scheme);
        }

        ct.ThrowIfCancellationRequested();

        if (!result.IsValid)
        {
            // IdentityModel diagnostics can contain token data. Do not attach its exception to a public failure.
            throw new OAuthProtocolException("invalid_id_token");
        }

        ValidateHash(claims, "at_hash", tokens.AccessToken, alg);
        ValidateHash(claims, "c_hash", code, alg);

        JsonElement? userInfo = null;
        if (_options.GetClaimsFromUserInfoEndpoint)
        {
            userInfo = await _client.GetUserInformationAsync(tokens, ct);
            if (OAuthProtocol.String(userInfo.Value, "sub") != OAuthProtocol.String(claims, "sub"))
            {
                throw new OAuthProtocolException("userinfo_subject_mismatch");
            }
        }

        ct.ThrowIfCancellationRequested();
        return new OpenIdConnectIdentity(new ClaimsPrincipal(result.ClaimsIdentity), claims, userInfo);
    }

    private async Task<TokenValidationResult> VerifyAsync(string                     token,
                                                          string                     alg,
                                                          string?                    kid,
                                                          OpenIdConnectConfiguration configuration,
                                                          string                     scheme)
    {
        if (!configuration.Algorithms.Contains(alg, StringComparer.Ordinal))
        {
            throw new OAuthProtocolException("unsupported_id_token_algorithm");
        }

        JsonWebKey[] keys;
        try
        {
            keys = configuration.Keys.Where(key => (kid is null                   || key.Kid                 == kid) &&
                                                   (string.IsNullOrEmpty(key.Alg) || key.Alg                 == alg) &&
                                                   (alg.StartsWith("ES", StringComparison.Ordinal) ? key.Kty == "EC" : key.Kty == "RSA" && key.KeySize >= 2048)).ToArray();
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException or CryptographicException)
        {
            throw new OAuthProtocolException("invalid_jwks");
        }

        var handler = new JsonWebTokenHandler
        {
            MapInboundClaims        = false,
            MaximumTokenSizeInBytes = _options.MaximumTokenBytes,
        };
        var parameters = new TokenValidationParameters
        {
            AuthenticationType                        = scheme,
            NameClaimType                             = "name",
            RoleClaimType                             = "roles",
            RequireSignedTokens                       = true,
            ValidateIssuerSigningKey                  = true,
            IssuerSigningKeys                         = keys,
            ValidAlgorithms                           = [alg],
            ValidateIssuer                            = true,
            ValidIssuer                               = _options.Issuer,
            ValidateAudience                          = true,
            ValidAudience                             = _oauth.ClientId,
            IgnoreTrailingSlashWhenValidatingAudience = false,
            RequireExpirationTime                     = true,
            ValidateLifetime                          = true,
            ClockSkew                                 = _options.ClockSkew,
            LifetimeValidator = (notBefore, expires, _, _) => expires is not null &&
                                                              expires > _time.GetUtcNow().UtcDateTime - _options.ClockSkew                           &&
                                                              (notBefore is null || notBefore <= _time.GetUtcNow().UtcDateTime + _options.ClockSkew) &&
                                                              (notBefore is null || notBefore <= expires),
            IncludeTokenOnFailedValidation = false,
            LogValidationExceptions        = false,
        };
        return await handler.ValidateTokenAsync(token, parameters);
    }

    private void ValidateClaims(JsonElement claims, string nonce)
    {
        var subject = OAuthProtocol.String(claims, "sub");
        if (OAuthProtocol.String(claims, "iss") != _options.Issuer ||
            string.IsNullOrEmpty(subject)                          ||
            subject.Length > 255                                   ||
            subject.Any(value => value > 127)                      ||
            !Equal(OAuthProtocol.String(claims, "nonce"), nonce))
        {
            throw new OAuthProtocolException("invalid_id_token_claims");
        }

        if (!claims.TryGetProperty("aud", out var audience))
        {
            throw new OAuthProtocolException("invalid_id_token_audience");
        }

        string[] audiences;
        if (audience.ValueKind == JsonValueKind.String)
        {
            audiences = [audience.GetString()!];
        }
        else if (audience.ValueKind == JsonValueKind.Array && audience.EnumerateArray().All(item => item.ValueKind == JsonValueKind.String))
        {
            audiences = audience.EnumerateArray().Select(item => item.GetString()!).ToArray();
        }
        else
        {
            throw new OAuthProtocolException("invalid_id_token_audience");
        }

        var party = OAuthProtocol.String(claims, "azp");
        if (!audiences.Contains(_oauth.ClientId, StringComparer.Ordinal)                                      ||
            audiences.Any(value => value != _oauth.ClientId && !_options.AdditionalAudiences.Contains(value)) ||
            (party is not null || audiences.Length > 1) && party != _oauth.ClientId)
        {
            throw new OAuthProtocolException("invalid_id_token_audience");
        }

        var now     = _time.GetUtcNow().ToUnixTimeSeconds();
        var skew    = (long)_options.ClockSkew.TotalSeconds;
        var issued  = Timestamp(claims, "iat");
        var expires = Timestamp(claims, "exp");
        if (issued > now + skew || expires <= now - skew || issued > expires)
        {
            throw new OAuthProtocolException("invalid_id_token_lifetime");
        }

        if (claims.TryGetProperty("nbf", out _) && Timestamp(claims, "nbf") > now + skew)
        {
            throw new OAuthProtocolException("invalid_id_token_lifetime");
        }

        if (claims.TryGetProperty("auth_time", out _) || _options.MaxAge is not null)
        {
            var authenticated = Timestamp(claims, "auth_time");
            if (authenticated > now + skew || _options.MaxAge is {} maxAge && now - authenticated > maxAge.TotalSeconds + skew)
            {
                throw new OAuthProtocolException("invalid_authentication_time");
            }
        }
    }

    private static long Timestamp(JsonElement claims, string name)
    {
        if (!claims.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var seconds) || seconds is < 0 or > 253402300799)
        {
            throw new OAuthProtocolException("invalid_id_token_lifetime");
        }

        return seconds;
    }

    private static JsonElement Decode(string part)
    {
        try
        {
            using var document = JsonDocument.Parse(Base64UrlEncoder.DecodeBytes(part), new JsonDocumentOptions
            {
                MaxDepth = 32
            });
            OAuthProtocol.ValidateObject(document.RootElement);
            return document.RootElement.Clone();
        }
        catch (Exception exception) when (exception is FormatException or JsonException or ArgumentException)
        {
            throw new OAuthProtocolException("invalid_id_token");
        }
    }

    private static void ValidateHash(JsonElement claims, string name, string value, string algorithm)
    {
        var expected = OAuthProtocol.String(claims, name);
        if (expected is null)
        {
            return;
        }

        if (value.Any(character => character > 127))
        {
            throw new OAuthProtocolException("invalid_token_hash");
        }

        var bytes = Encoding.ASCII.GetBytes(value);
        var hash = algorithm[2..] switch
        {
            "256" => SHA256.HashData(bytes),
            "384" => SHA384.HashData(bytes),
            "512" => SHA512.HashData(bytes),
            _     => throw new OAuthProtocolException("unsupported_id_token_algorithm"),
        };
        if (!Equal(expected, OAuthProtocol.Base64Url(hash[..(hash.Length / 2)])))
        {
            throw new OAuthProtocolException("invalid_token_hash");
        }
    }

    private static bool Equal(string? actual, string expected)
        => actual is not null && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(actual), Encoding.UTF8.GetBytes(expected));
}
