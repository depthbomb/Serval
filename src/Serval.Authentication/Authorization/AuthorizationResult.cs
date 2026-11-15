#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

namespace Serval.Authentication.Authorization;

/// <summary>Explains why a resource permission was not granted.</summary>
public enum AuthorizationFailureKind
{
    /// <summary>A voter explicitly denied the operation.</summary>
    Denied,

    /// <summary>No voter granted the operation; all abstained or none were registered.</summary>
    NoApplicableVoter,
}

/// <summary>Contains the combined resource-authorization decision.</summary>
public sealed class AuthorizationResult
{
    /// <summary>Whether the permission was granted.</summary>
    public bool Succeeded => FailureKind is null;

    /// <summary>The reason category, or null for a grant.</summary>
    public AuthorizationFailureKind? FailureKind { get; }

    /// <summary>An optional diagnostic reason. The framework does not send it to clients automatically.</summary>
    public string? FailureReason { get; }

    private static readonly AuthorizationResult Successful = new(null, null);

    private AuthorizationResult(AuthorizationFailureKind? failureKind, string? failureReason)
    {
        FailureKind   = failureKind;
        FailureReason = failureReason;
    }

    /// <summary>Returns the shared successful authorization result.</summary>
    /// <returns>A result with no failure.</returns>
    public static AuthorizationResult Success() => Successful;

    /// <summary>Creates a failed resource-authorization result.</summary>
    /// <param name="kind">The declared failure category.</param>
    /// <param name="reason">An optional application-facing diagnostic reason.</param>
    /// <returns>The failed result.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The failure kind is not a declared enum value.</exception>
    public static AuthorizationResult Fail(AuthorizationFailureKind kind, string? reason = null)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        return new AuthorizationResult(kind, reason);
    }
}
