#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

namespace Serval.Authentication.Authorization;

/// <summary>Represents one voter's decision about a resource permission.</summary>
public enum AuthorizationVoteDecision
{
    /// <summary>The voter does not decide this resource/permission pair.</summary>
    Abstain,

    /// <summary>The voter permits the operation, unless another voter denies it.</summary>
    Grant,

    /// <summary>The voter rejects the operation, overriding any grants.</summary>
    Deny,
}

/// <summary>Carries one voter's decision and optional diagnostic reason.</summary>
public sealed class AuthorizationVote
{
    /// <summary>Whether this voter abstains, grants, or denies.</summary>
    public AuthorizationVoteDecision Decision { get; }

    /// <summary>An optional diagnostic explanation, normally attached to a denial.</summary>
    public string? Reason { get; }

    private static readonly AuthorizationVote Granted   = new(AuthorizationVoteDecision.Grant);
    private static readonly AuthorizationVote Abstained = new(AuthorizationVoteDecision.Abstain);

    private AuthorizationVote(AuthorizationVoteDecision decision, string? reason = null)
    {
        Decision = decision;
        Reason   = reason;
    }

    /// <summary>Returns a vote permitting the operation.</summary>
    /// <returns>The shared grant vote.</returns>
    public static AuthorizationVote Grant() => Granted;

    /// <summary>Returns a vote rejecting the operation.</summary>
    /// <param name="reason">An optional diagnostic reason.</param>
    /// <returns>A denial that takes precedence over grants.</returns>
    public static AuthorizationVote Deny(string? reason = null) => new(AuthorizationVoteDecision.Deny, reason);

    /// <summary>Returns a vote that leaves this decision to other voters.</summary>
    /// <returns>The shared abstention vote.</returns>
    public static AuthorizationVote Abstain() => Abstained;
}
