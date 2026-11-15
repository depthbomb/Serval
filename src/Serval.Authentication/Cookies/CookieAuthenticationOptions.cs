#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Serval.Http;

namespace Serval.Authentication.Cookies;

/// <summary>Configures opaque cookie credentials backed by server-side session tickets.</summary>
public sealed class CookieAuthenticationOptions
{
    /// <summary>
    ///     The session cookie name. Defaults to __Host-Serval.Session; AddCookie supplies a scheme-specific default.
    /// </summary>
    public string CookieName { get; set; } = "__Host-Serval.Session";

    /// <summary>
    ///     Cookie flags, path, and domain. Set expiration through session properties rather than Cookie.Expires or
    ///     Cookie.MaxAge.
    /// </summary>
    public CookieOptions Cookie { get; set; } = new();

    /// <summary>The positive default session lifetime, initially eight hours.</summary>
    public TimeSpan Lifetime { get; set; } = TimeSpan.FromHours(8);

    /// <summary>
    ///     The absolute lifetime from original issuance, initially seven days. Must be at least Lifetime and at most 365
    ///     days.
    /// </summary>
    public TimeSpan MaximumLifetime { get; set; } = TimeSpan.FromDays(7);

    /// <summary>
    ///     Whether refreshable sessions are renewed when less than half of Lifetime remains. Defaults to false.
    /// </summary>
    public bool SlidingExpiration { get; set; }

    /// <summary>
    ///     An optional callback to recheck a stored ticket. Success may replace claims; failure removes the session.
    /// </summary>
    public Func<RequestContext, AuthenticationTicket, ValueTask<AuthenticationResult>>? ValidateTicket { get; set; }

    /// <summary>An optional replacement for the default 401 challenge.</summary>
    public Func<RequestContext, AuthenticationProperties, ValueTask>? OnChallenge { get; set; }

    /// <summary>An optional replacement for the default 403 denial.</summary>
    public Func<RequestContext, ValueTask>? OnForbidden { get; set; }

    internal void Validate()
    {
        if (Lifetime <= TimeSpan.Zero || MaximumLifetime < Lifetime || MaximumLifetime > TimeSpan.FromDays(365))
        {
            throw new ArgumentOutOfRangeException(nameof(Lifetime));
        }

        ArgumentNullException.ThrowIfNull(Cookie);

        if (Cookie.MaxAge is not null || Cookie.Expires is not null)
        {
            throw new ArgumentException("Use the authentication lifetime and persistence options to set cookie expiration.");
        }

        new ResponseCookies(new Headers()).Set(CookieName, "validation", Cookie);
    }
}
