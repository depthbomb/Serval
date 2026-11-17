#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using System.Text.Json;

namespace Serval.OAuth;

// These types deliberately do not print credentials through record-generated ToString methods.
/// <summary>Carries token credentials and provider response data.</summary>
/// <remarks>
///     Keep token values server-side. This class does not include credentials in a generated record ToString
///     implementation.
/// </remarks>
public sealed class OAuthTokenResponse
{
    /// <summary>The required nonempty access token.</summary>
    public required string AccessToken { get; init; }

    /// <summary>The required nonempty token type, such as Bearer.</summary>
    public required string TokenType { get; init; }

    /// <summary>The optional refresh token returned by the provider.</summary>
    public string? RefreshToken { get; init; }

    /// <summary>The unvalidated ID token. Browser OIDC completion validates it before creating a session.</summary>
    public string? IdToken { get; init; }

    /// <summary>The optional nonnegative token lifetime measured from issuance.</summary>
    public TimeSpan? ExpiresIn { get; init; }

    /// <summary>The provider's optional space-separated granted scope string.</summary>
    public string? Scope { get; init; }

    /// <summary>The detached provider JSON response, including provider-specific fields.</summary>
    public JsonElement AdditionalData { get; init; }
}

/// <summary>Contains an authorization URL and the state and PKCE verifier needed to finish it.</summary>
public sealed class OAuthAuthorizationRequest
{
    /// <summary>The provider authorization URL, including the S256 PKCE challenge.</summary>
    public Uri AuthorizationUri { get; }

    /// <summary>The state value to validate on the callback.</summary>
    public string State { get; }

    /// <summary>The server-held PKCE verifier to send only during code exchange.</summary>
    public string CodeVerifier { get; }

    /// <summary>Creates an authorization request with its state and PKCE verifier.</summary>
    /// <param name="authorizationUri">The URL to which the browser should be redirected.</param>
    /// <param name="state">The state value to bind to this authorization attempt.</param>
    /// <param name="codeVerifier">The secret PKCE verifier retained until code exchange.</param>
    public OAuthAuthorizationRequest(Uri authorizationUri, string state, string codeVerifier)
    {
        AuthorizationUri = authorizationUri;
        State            = state;
        CodeVerifier     = codeVerifier;
    }
}

/// <summary>Reports an OAuth failure without including credential-bearing response bodies.</summary>
public sealed class OAuthProtocolException : Exception
{
    /// <summary>The provider error identifier or framework validation error code.</summary>
    public string Error { get; }

    /// <summary>The backchannel HTTP status, when available.</summary>
    public int? StatusCode { get; }

    /// <summary>Creates a protocol error with a generic exception message.</summary>
    /// <param name="error">The error identifier.</param>
    /// <param name="statusCode">The optional backchannel HTTP status.</param>
    public OAuthProtocolException(string error, int? statusCode = null) : base("The OAuth operation failed.")
    {
        Error      = error;
        StatusCode = statusCode;
    }
}
