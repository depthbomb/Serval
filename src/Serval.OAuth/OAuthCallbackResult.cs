#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Serval.Authentication;

namespace Serval.OAuth;

/// <summary>A completed browser exchange before application account mapping and local sign-in.</summary>
public sealed class OAuthCallbackResult
{
    /// <summary>Provider tokens, client, and validated OIDC identity when using OIDC.</summary>
    /// <remarks>Plain OAuth requires application-specific identity verification. Do not send provider tokens to the browser.</remarks>
    public OAuthSignInContext SignInContext { get; }

    /// <summary>The local destination validated when this attempt began.</summary>
    public string ReturnUrl { get; }

    /// <summary>The local-session properties supplied when this attempt began.</summary>
    public AuthenticationProperties Properties { get; }

    internal OAuthCallbackResult(OAuthSignInContext signInContext, string returnUrl, AuthenticationProperties properties)
    {
        SignInContext = signInContext;
        ReturnUrl     = returnUrl;
        Properties    = properties;
    }

    /// <summary>Returns the type name without disclosing provider credentials.</summary>
    public override string ToString() => nameof(OAuthCallbackResult);
}
