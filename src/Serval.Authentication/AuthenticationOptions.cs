#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Serval.Authentication.Authorization;

namespace Serval.Authentication;

/// <summary>Selects default schemes and authorization policies.</summary>
public sealed class AuthenticationOptions
{
    /// <summary>The fallback scheme when an operation-specific default or explicit scheme is absent.</summary>
    public string? DefaultScheme { get; set; }

    /// <summary>The default for authentication and forbidding access; null falls back to DefaultScheme.</summary>
    public string? DefaultAuthenticateScheme { get; set; }

    /// <summary>The default for authentication challenges; null falls back to DefaultScheme.</summary>
    public string? DefaultChallengeScheme { get; set; }

    /// <summary>The default for sign-in and sign-out; null falls back to DefaultScheme.</summary>
    public string? DefaultSignInScheme { get; set; }

    /// <summary>The policy used without a name. Initially requires an authenticated user.</summary>
    public AuthorizationPolicy DefaultPolicy { get; set; } = new();

    /// <summary>Named authorization policies, looked up using case-sensitive names.</summary>
    public IDictionary<string, AuthorizationPolicy> Policies { get; } = new Dictionary<string, AuthorizationPolicy>(StringComparer.Ordinal);

    internal HashSet<string> Schemes { get; } = new(StringComparer.Ordinal);
}
