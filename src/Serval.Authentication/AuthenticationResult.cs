#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using System.Security.Claims;

namespace Serval.Authentication;

/// <summary>Carries session lifetime, persistence, and application metadata.</summary>
public sealed record AuthenticationProperties
{
    /// <summary>Whether a cookie session should survive browser closure. Defaults to false.</summary>
    public bool IsPersistent { get; init; }

    /// <summary>Whether a handler may renew this session. Defaults to true.</summary>
    public bool AllowRefresh { get; init; } = true;

    /// <summary>The original issuance time, or null for the handler to supply it.</summary>
    public DateTimeOffset? IssuedUtc { get; init; }

    /// <summary>The expiration time, or null to use the handler's configured lifetime.</summary>
    public DateTimeOffset? ExpiresUtc { get; init; }

    /// <summary>Application metadata stored with the ticket. Cookie sessions also keep their CSRF token here.</summary>
    public IReadOnlyDictionary<string, string> Items { get; init; } = new Dictionary<string, string>();
}

/// <summary>Carries an authenticated principal, scheme, and session properties.</summary>
/// <param name="Principal">The principal with at least one authenticated identity.</param>
/// <param name="Scheme">The authentication scheme that issued the ticket.</param>
/// <param name="Properties">Lifetime and application metadata for the ticket.</param>
public sealed record AuthenticationTicket(ClaimsPrincipal Principal, string Scheme, AuthenticationProperties Properties)
{
    /// <summary>Copies identities, claims, and the properties dictionary to isolate mutable request state.</summary>
    /// <returns>A ticket whose identities and item dictionary can be changed independently.</returns>
    public AuthenticationTicket Copy() => new(new ClaimsPrincipal(Principal.Identities.Select(identity => identity.Clone())), Scheme,
        Properties with { Items = new Dictionary<string, string>(Properties.Items, StringComparer.Ordinal) });
}

/// <summary>Distinguishes successful, absent, and rejected credentials.</summary>
public sealed class AuthenticationResult
{
    /// <summary>The authenticated ticket, or null when credentials are absent or rejected.</summary>
    public AuthenticationTicket? Ticket { get; }

    /// <summary>The rejection reason, or null for success or absent credentials.</summary>
    public string? Failure { get; }

    /// <summary>Whether a ticket was returned.</summary>
    public bool Succeeded => Ticket is not null;

    /// <summary>Whether no credentials were found and no failure was reported.</summary>
    public bool IsMissing => Ticket is null && Failure is null;

    private AuthenticationResult(AuthenticationTicket? ticket, string? failure)
    {
        Ticket  = ticket;
        Failure = failure;
    }

    /// <summary>Creates a successful result for an authenticated ticket.</summary>
    /// <param name="ticket">A ticket with at least one authenticated identity.</param>
    /// <returns>A successful result retaining the supplied ticket.</returns>
    /// <exception cref="ArgumentNullException">The ticket is null.</exception>
    /// <exception cref="ArgumentException">The principal has no authenticated identity.</exception>
    public static AuthenticationResult Success(AuthenticationTicket ticket)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        if (!ticket.Principal.Identities.Any(identity => identity.IsAuthenticated))
        {
            throw new ArgumentException("An authenticated identity is required.", nameof(ticket));
        }

        return new AuthenticationResult(ticket, null);
    }

    /// <summary>Creates a result for a request without applicable credentials.</summary>
    /// <returns>A result with no ticket and no failure.</returns>
    public static AuthenticationResult Missing() => new(null, null);

    /// <summary>Creates a result for rejected credentials.</summary>
    /// <param name="reason">A nonempty diagnostic reason.</param>
    /// <returns>A failed authentication result.</returns>
    /// <exception cref="ArgumentException">The reason is empty or whitespace.</exception>
    public static AuthenticationResult Fail(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new AuthenticationResult(null, reason);
    }
}
