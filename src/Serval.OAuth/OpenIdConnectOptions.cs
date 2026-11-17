#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using System.Security.Claims;
using System.Text.Json;

namespace Serval.OAuth;

/// <summary>Configures OpenID Connect discovery and signed ID tokens for the authorization-code flow.</summary>
public sealed class OpenIdConnectOptions
{
    /// <summary>The exact trusted issuer identifier, including any path and trailing slash.</summary>
    public string Issuer { get; set; } = "";

    /// <summary>An optional discovery URL. Defaults to the issuer followed by /.well-known/openid-configuration.</summary>
    public Uri? MetadataAddress { get; set; }

    /// <summary>Accepted asymmetric signing algorithms. Defaults to RS256; unsigned and HMAC tokens are unsupported.</summary>
    public ISet<string> SigningAlgorithms { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "RS256"
    };

    /// <summary>Additional trusted audiences. The client ID must still be present and azp must identify the client.</summary>
    public ISet<string> AdditionalAudiences { get; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Allowed clock difference for token timestamps. Defaults to one minute, at most five minutes.</summary>
    public TimeSpan ClockSkew { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Optional maximum authentication age, sent as max_age and checked against auth_time.</summary>
    public TimeSpan? MaxAge { get; set; }

    /// <summary>Whether completion fetches UserInfo and requires its sub to match the ID token. Defaults to false.</summary>
    public bool GetClaimsFromUserInfoEndpoint { get; set; }

    /// <summary>Discovery and signing-key cache lifetime. Defaults to twelve hours; minimum one minute.</summary>
    public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromHours(12);

    /// <summary>Minimum interval between forced signing-key refresh attempts. Defaults to thirty seconds.</summary>
    public TimeSpan RefreshCooldown { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Maximum compact ID-token length. Defaults to 64 KiB, at most 1 MiB.</summary>
    public int MaximumTokenBytes { get; set; } = 64 * 1024;

    internal void Validate(OAuthOptions options)
    {
        if (!Uri.TryCreate(Issuer, UriKind.Absolute, out var issuer) || issuer.Query.Length != 0)
        {
            throw new ArgumentException("An absolute OIDC issuer without a query is required.", nameof(Issuer));
        }

        options.ValidateUri(issuer, nameof(Issuer));
        options.ValidateUri(MetadataAddress ?? new Uri(Issuer.TrimEnd('/') + "/.well-known/openid-configuration"), nameof(MetadataAddress));
        if (options.AuthorizationEndpoint is not null   ||
            options.TokenEndpoint is not null           ||
            options.UserInformationEndpoint is not null ||
            options.RevocationEndpoint is not null)
        {
            throw new ArgumentException("OIDC endpoints come from discovery; configure the issuer and metadata address instead.");
        }

        if (SigningAlgorithms.Count == 0 || SigningAlgorithms.Any(value => value is not ("RS256" or "RS384" or "RS512" or "PS256" or "PS384" or "PS512" or "ES256" or "ES384" or "ES512")))
        {
            throw new ArgumentException("OIDC requires an explicitly allowed RSA or ECDSA signing algorithm.");
        }

        if (ClockSkew < TimeSpan.Zero                                                                                                ||
            ClockSkew > TimeSpan.FromMinutes(5)                                                                                      ||
            MaxAge is {} age && (age < TimeSpan.Zero || age.TotalSeconds > int.MaxValue || age.Ticks % TimeSpan.TicksPerSecond != 0) ||
            RefreshInterval < TimeSpan.FromMinutes(1)                                                                                || RefreshInterval > TimeSpan.FromDays(7) ||
            RefreshCooldown < TimeSpan.FromSeconds(1)                                                                                || RefreshCooldown > RefreshInterval      ||
            MaximumTokenBytes is < 1024 or > 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(OpenIdConnectOptions), "Invalid OIDC time or size limits.");
        }

        if (AdditionalAudiences.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("Additional audiences must be nonempty identifiers.");
        }

        if (options.ExpectedIssuer is not null && options.ExpectedIssuer != Issuer)
        {
            throw new ArgumentException("The callback issuer must match the OIDC issuer.");
        }

        ValidateParameters(options.AuthorizationParameters);
    }

    internal static void ValidateParameters(IEnumerable<KeyValuePair<string, string>>? parameters)
    {
        if (parameters?.Any(pair => pair.Key.Equals("nonce", StringComparison.OrdinalIgnoreCase)   ||
                                    pair.Key.Equals("max_age", StringComparison.OrdinalIgnoreCase) ||
                                    pair.Key.Equals("request", StringComparison.OrdinalIgnoreCase) ||
                                    pair.Key.Equals("request_uri", StringComparison.OrdinalIgnoreCase)) == true)
        {
            throw new ArgumentException("OIDC nonce and max_age are managed by the handler; request objects are unsupported.");
        }
    }
}

/// <summary>Provider identity available only after ID-token and optional UserInfo validation.</summary>
public sealed class OpenIdConnectIdentity
{
    /// <summary>The principal with original claim names. Account mapping may replace it before local sign-in.</summary>
    public ClaimsPrincipal Principal { get; }

    /// <summary>The validated ID-token payload, with JSON claim types preserved.</summary>
    public JsonElement Claims { get; }

    /// <summary>Optional UserInfo claims whose subject has been checked against the ID token.</summary>
    public JsonElement? UserInfo { get; }

    internal OpenIdConnectIdentity(ClaimsPrincipal principal, JsonElement claims, JsonElement? userInfo)
    {
        Principal = principal;
        Claims    = claims;
        UserInfo  = userInfo;
    }
}
