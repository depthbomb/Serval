#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using System.Security.Claims;

namespace Serval.Authentication.Cookies;

/// <summary>Authenticates opaque session cookies and stores tickets outside the browser.</summary>
/// <remarks>
///     The store receives a scheme-scoped SHA-256 key rather than the browser credential. Sign-in rotates the credential
///     and CSRF token.
/// </remarks>
public sealed class CookieAuthenticationHandler : AuthenticationHandler, ISignInHandler
{
    internal const string CsrfProperty = CsrfTokens.PropertyName;

    private readonly object                       _currentTokenKey = new();
    private readonly CookieAuthenticationOptions  _options;
    private readonly AuthenticationSessionService _sessions;

    /// <summary>Creates a cookie handler with a session store and clock.</summary>
    /// <param name="options">Validated cookie and lifetime settings.</param>
    /// <param name="store">The store used for ticket lookup, renewal, and revocation.</param>
    /// <param name="timeProvider">The clock used for session expiration.</param>
    /// <exception cref="ArgumentException">Cookie attributes or expiration options are invalid.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Session lifetime limits are invalid.</exception>
    public CookieAuthenticationHandler(CookieAuthenticationOptions options,
                                       IAuthenticationSessionStore store,
                                       TimeProvider                timeProvider)
    {
        options.Validate();
        _options = options;
        _sessions = new AuthenticationSessionService(store, timeProvider, new AuthenticationSessionOptions
        {
            Lifetime        = options.Lifetime,
            MaximumLifetime = options.MaximumLifetime,
        });
    }

    /// <inheritdoc />
    /// <remarks>
    ///     A successful validation callback can replace the principal but cannot extend the original absolute lifetime.
    ///     Sliding renewal is skipped after response startup.
    /// </remarks>
    public override async ValueTask<AuthenticationResult> AuthenticateAsync(RequestContext context, string scheme)
    {
        var token = GetToken(context);
        if (token is null)
        {
            return AuthenticationResult.Missing();
        }

        if (_options.Cookie.Secure && context.Request.Url.Scheme != Uri.UriSchemeHttps)
        {
            return AuthenticationResult.Fail("Secure session cookies require HTTPS.");
        }

        var validation = await _sessions.ValidateAsync(scheme, token, context.RequestAborted);
        if (validation.Ticket is not {} ticket)
        {
            return validation;
        }

        if (_options.ValidateTicket is not null)
        {
            var result = await _options.ValidateTicket(context, ticket);
            if (!result.Succeeded)
            {
                await _sessions.RevokeAsync(scheme, token, context.RequestAborted);
                return result;
            }

            // Validation can replace claims, but cannot extend a credential's original lifetime.
            ticket = ticket with
            {
                Principal = result.Ticket!.Principal,
            };
        }

        if (_options.SlidingExpiration && _sessions.NeedsRenewal(ticket) && !context.Response.HasStarted)
        {
            var renewed = await _sessions.RenewAsync(scheme, token, ticket.Principal, context.RequestAborted);
            if (renewed.Ticket is not {} renewal)
            {
                return renewed;
            }

            ticket = renewal;
            SetCookie(context, token, ticket.Properties);
        }

        return AuthenticationResult.Success(ticket);
    }

    /// <inheritdoc />
    public override ValueTask ChallengeAsync(RequestContext context, string scheme, AuthenticationProperties properties)
        => _options.OnChallenge?.Invoke(context, properties) ?? base.ChallengeAsync(context, scheme, properties);

    /// <inheritdoc />
    public override ValueTask ForbidAsync(RequestContext context, string scheme)
        => _options.OnForbidden?.Invoke(context) ?? base.ForbidAsync(context, scheme);

    /// <inheritdoc />
    /// <remarks>
    ///     Creates a new credential, removes the previous valid credential, and writes the cookie. Secure cookies require
    ///     an HTTPS request; session expiration must fit within MaximumLifetime.
    /// </remarks>
    public async ValueTask<AuthenticationTicket> SignInAsync(RequestContext           context,
                                                             string                   scheme,
                                                             ClaimsPrincipal          principal,
                                                             AuthenticationProperties properties)
    {
        EnsureWritable(context);
        if (_options.Cookie.Secure && context.Request.Url.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("Secure session cookies require an HTTPS request.");
        }

        var items = new Dictionary<string, string>(properties.Items, StringComparer.Ordinal);
        CsrfTokens.Rotate(items);

        var session = await _sessions.IssueAsync(scheme, principal, properties with
        {
            Items = items,
        }, GetToken(context), context.RequestAborted);

        var token  = session.Credential;
        var ticket = session.Ticket;

        context.Items[_currentTokenKey] = token;

        SetCookie(context, token, ticket.Properties);

        return ticket;
    }

    /// <inheritdoc />
    /// <remarks>Removes the stored credential and expires the cookie. Must run before response startup.</remarks>
    public async ValueTask SignOutAsync(RequestContext context, string scheme)
    {
        EnsureWritable(context);
        var token = GetToken(context);
        await _sessions.RevokeAsync(scheme, token, context.RequestAborted);

        context.Items[_currentTokenKey] = null;
        context.Response.Cookies.Delete(_options.CookieName, _options.Cookie);
    }

    // The incoming Cookie header can't change. Track rotations here so sign-in followed by sign-out
    // in one request revokes the new credential, not the one the browser originally sent.
    private string? GetToken(RequestContext context)
        => context.Items.TryGetValue(_currentTokenKey, out var current)
            ? (string?)current
            : context.Request.Cookies.GetValueOrDefault(_options.CookieName);

    private void SetCookie(RequestContext context, string token, AuthenticationProperties properties)
        => context.Response.Cookies.Set(_options.CookieName, token, _options.Cookie with
        {
            Expires = properties.IsPersistent ? properties.ExpiresUtc : null,
        });

    private static void EnsureWritable(RequestContext context)
    {
        if (context.Response.HasStarted)
        {
            throw new InvalidOperationException("Sign-in and sign-out must run before the response starts.");
        }
    }
}
