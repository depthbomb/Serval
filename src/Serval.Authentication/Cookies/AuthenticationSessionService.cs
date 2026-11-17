#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;

namespace Serval.Authentication.Cookies;

/// <summary>Lifetime limits for opaque sessions, independent of credential transport.</summary>
public sealed class AuthenticationSessionOptions
{
    /// <summary>The default lifetime, initially eight hours.</summary>
    public TimeSpan Lifetime { get; init; } = TimeSpan.FromHours(8);
    /// <summary>The absolute lifetime from issuance, initially seven days.</summary>
    public TimeSpan MaximumLifetime { get; init; } = TimeSpan.FromDays(7);
}

/// <summary>A newly issued opaque credential and its isolated ticket. Treat Credential as a secret.</summary>
public sealed class IssuedAuthenticationSession
{
    /// <summary>The credential to transmit over the application's chosen secure transport.</summary>
    public string Credential { get; }
    /// <summary>The issued ticket.</summary>
    public AuthenticationTicket Ticket { get; }

    internal IssuedAuthenticationSession(string credential, AuthenticationTicket ticket)
    {
        Credential = credential;
        Ticket     = ticket;
    }

    /// <summary>Returns the type name without exposing the credential.</summary>
    public override string ToString() => nameof(IssuedAuthenticationSession);
}

/// <summary>Issues, validates, renews, and revokes server-side sessions without reading or writing HTTP cookies.</summary>
public sealed class AuthenticationSessionService
{
    private readonly IAuthenticationSessionStore _store;
    private readonly TimeProvider _time;
    private readonly AuthenticationSessionOptions _options;

    /// <summary>Creates a session service using application-selected storage, clock, and lifetime limits.</summary>
    public AuthenticationSessionService(IAuthenticationSessionStore store, TimeProvider time, AuthenticationSessionOptions? options = null)
    {
        _store   = store ?? throw new ArgumentNullException(nameof(store));
        _time    = time ?? throw new ArgumentNullException(nameof(time));
        _options = options ?? new AuthenticationSessionOptions();
        if (_options.Lifetime <= TimeSpan.Zero || _options.MaximumLifetime < _options.Lifetime || _options.MaximumLifetime > TimeSpan.FromDays(365))
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }
    }

    /// <summary>Validates the credential, scheme, and stored lifetime without renewing or assigning a principal.</summary>
    public async ValueTask<AuthenticationResult> ValidateAsync(string scheme, string? credential, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scheme);
        ct.ThrowIfCancellationRequested();
        if (credential is null)
        {
            return AuthenticationResult.Missing();
        }

        if (!ValidCredential(credential))
        {
            return AuthenticationResult.Fail("Invalid session credential.");
        }

        var ticket = await _store.FindAsync(Key(scheme, credential), ct);
        var now = _time.GetUtcNow();
        if (ticket is null || ticket.Scheme != scheme || ticket.Properties.IssuedUtc is not {} issued || issued > now ||
            ticket.Properties.ExpiresUtc is not {} expires || expires <= now || expires > issued + _options.MaximumLifetime || issued + _options.MaximumLifetime <= now)
        {
            return AuthenticationResult.Fail("The session has expired or was revoked.");
        }

        return AuthenticationResult.Success(ticket.Copy());
    }

    /// <summary>Creates a credential, then revokes a previous credential if supplied. A failed store preserves the old session.</summary>
    public async ValueTask<IssuedAuthenticationSession> IssueAsync(string scheme, ClaimsPrincipal principal, AuthenticationProperties? properties = null, string? previousCredential = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scheme);
        ArgumentNullException.ThrowIfNull(principal);
        properties ??= new AuthenticationProperties();
        var now = _time.GetUtcNow();
        var issued = properties.IssuedUtc ?? now;
        var expires = properties.ExpiresUtc ?? now + _options.Lifetime;
        if (issued > now || issued + _options.MaximumLifetime <= now || expires <= now || expires > issued + _options.MaximumLifetime)
        {
            throw new ArgumentException("The session lifetime is invalid.", nameof(properties));
        }

        var ticket = new AuthenticationTicket(principal, scheme, properties with { IssuedUtc = issued, ExpiresUtc = expires }).Copy();
        _ = AuthenticationResult.Success(ticket);
        var credential = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        await _store.StoreAsync(Key(scheme, credential), ticket, ct);
        await RevokeAsync(scheme, previousCredential, ct);
        return new IssuedAuthenticationSession(credential, ticket);
    }

    /// <summary>Reports whether a refreshable ticket has less than half the default lifetime left.</summary>
    public bool NeedsRenewal(AuthenticationTicket ticket)
        => ticket.Properties.AllowRefresh && ticket.Properties.ExpiresUtc - _time.GetUtcNow() < _options.Lifetime / 2;

    /// <summary>Renews a live credential within its original absolute lifetime, optionally replacing its principal.</summary>
    /// <remarks>A revoked credential is never recreated. The original issuance and application metadata come from storage.</remarks>
    public async ValueTask<AuthenticationResult> RenewAsync(string scheme, string credential, ClaimsPrincipal? principal = null, CancellationToken ct = default)
    {
        var current = await ValidateAsync(scheme, credential, ct);
        if (current.Ticket is not {} ticket || !ticket.Properties.AllowRefresh)
        {
            return current;
        }

        var expiry = _time.GetUtcNow() + _options.Lifetime;
        var maximum = ticket.Properties.IssuedUtc!.Value + _options.MaximumLifetime;
        var renewed = ticket with
        {
            Principal = principal ?? ticket.Principal,
            Properties = ticket.Properties with { ExpiresUtc = expiry < maximum ? expiry : maximum },
        };
        var result = AuthenticationResult.Success(renewed.Copy());
        return await _store.RenewAsync(Key(scheme, credential), renewed, ct) ? result : AuthenticationResult.Fail("The session was revoked.");
    }

    /// <summary>Revokes a credential. Missing or malformed credentials require no store operation.</summary>
    public ValueTask RevokeAsync(string scheme, string? credential, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scheme);
        ct.ThrowIfCancellationRequested();
        return credential is not null && ValidCredential(credential) ? _store.RemoveAsync(Key(scheme, credential), ct) : ValueTask.CompletedTask;
    }

    private static bool ValidCredential(string token) => token.Length == 43 && token.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private static string Key(string scheme, string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scheme + "\0" + token)));
}
