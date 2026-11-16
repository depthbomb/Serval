#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Serval.OAuth;

internal static class OAuthProtocol
{
    private static readonly HashSet<string> Reserved =
    [
        with(StringComparer.OrdinalIgnoreCase),
        "client_id",
        "client_secret",
        "redirect_uri",
        "response_type",
        "response_mode",
        "scope",
        "state",
        "code_challenge",
        "code_challenge_method",
        "code_verifier",
        "grant_type",
        "code",
        "refresh_token",
        "token",
        "token_type_hint",
    ];

    internal static string RandomToken() => Base64Url(RandomNumberGenerator.GetBytes(32));

    internal static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    internal static string Challenge(string verifier)
    {
        if (verifier.Length is < 43 or > 128 || verifier.Any(character => !char.IsAsciiLetterOrDigit(character) && !"-._~".Contains(character)))
        {
            throw new ArgumentException("A PKCE verifier must contain 43 to 128 unreserved ASCII characters.", nameof(verifier));
        }

        return Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
    }

    internal static string Scope(IEnumerable<string> scopes)
    {
        var values = scopes.ToArray();
        if (values.Any(value => string.IsNullOrEmpty(value) || value.Any(character => character is < '!' or > '~' or '"' or '\\')))
        {
            throw new ArgumentException("OAuth scope values must be nonempty ASCII tokens.", nameof(scopes));
        }

        return string.Join(' ', values.Distinct(StringComparer.Ordinal));
    }

    internal static void AddParameters(IDictionary<string, string> target, IEnumerable<KeyValuePair<string, string>> additional)
    {
        foreach (var pair in additional)
        {
            if (string.IsNullOrWhiteSpace(pair.Key) || Reserved.Contains(pair.Key) || pair.Value is null || !target.TryAdd(pair.Key, pair.Value))
            {
                throw new ArgumentException($"OAuth parameter '{pair.Key}' is reserved or duplicated.", nameof(additional));
            }
        }
    }

    internal static Uri AuthorizationUri(Uri endpoint, Dictionary<string, string> parameters)
    {
        foreach (var part in endpoint.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var split = part.Split('=', 2);
            var name  = Uri.UnescapeDataString(split[0].Replace('+', ' '));
            var value = split.Length == 2 ? Uri.UnescapeDataString(split[1].Replace('+', ' ')) : "";
            AddParameters(parameters, [new KeyValuePair<string, string>(name, value)]);
        }

        return new UriBuilder(endpoint)
        {
            Query = string.Join('&', parameters.Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value))),
        }.Uri;
    }

    internal static void ValidateObject(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new OAuthProtocolException("invalid_response");
        }

        // Different JSON consumers choose different duplicate values. Reject that ambiguity at the boundary.
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (!names.Add(property.Name))
            {
                throw new OAuthProtocolException("invalid_response");
            }
        }
    }

    internal static string? String(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value))
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw new OAuthProtocolException("invalid_response");
        }

        return value.GetString();
    }
}
