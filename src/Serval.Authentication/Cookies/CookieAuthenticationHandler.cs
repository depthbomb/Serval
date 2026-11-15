#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;

namespace Serval.Authentication.Cookies;

/// <summary>Authenticates opaque session cookies and stores tickets outside the browser.</summary>
/// <remarks>
///     The store receives a scheme-scoped SHA-256 key rather than the browser credential. Sign-in rotates the credential
///     and CSRF token.
/// </remarks>
public sealed class CookieAuthenticationHandler : AuthenticationHandler, ISignInHandler
{
    internal const string CsrfProperty = "Serval.CsrfToken";

    private readonly object                      _currentTokenKey = new();
    private readonly CookieAuthenticationOptions _options;
    private readonly IAuthenticationSessionStore _store;
    private readonly TimeProvider                _time;

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
        _store   = store;
        _time    = timeProvider;
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

        if (!ValidToken(token) || _options.Cookie.Secure && context.Request.Url.Scheme != Uri.UriSchemeHttps)
        {
            return AuthenticationResult.Fail("Invalid session credential.");
        }

        var key    = Key(scheme, token);
        var ticket = await _store.FindAsync(key, context.RequestAborted);
        var now    = _time.GetUtcNow();
        if (
            ticket is null                                  ||
            ticket.Scheme != scheme                         ||
            ticket.Properties.IssuedUtc is not { } issued   ||
            issued > now                                    ||
            ticket.Properties.ExpiresUtc is not { } expires ||
            expires                           <= now        ||
            issued + _options.MaximumLifetime <= now
        )
        {
            return AuthenticationResult.Fail("The session has expired or was revoked.");
        }

        if (_options.ValidateTicket is not null)
        {
            var result = await _options.ValidateTicket(context, ticket);
            if (!result.Succeeded)
            {
                await _store.RemoveAsync(key, context.RequestAborted);
                return result;
            }

            // Validation can replace claims, but cannot extend a credential's original lifetime.
            ticket = ticket with { Principal = result.Ticket!.Principal };
        }

        if (_options.SlidingExpiration && ticket.Properties.AllowRefresh && expires - now < _options.Lifetime / 2 && !context.Response.HasStarted)
        {
            var renewedExpiry = now    + _options.Lifetime;
            var maximumExpiry = issued + _options.MaximumLifetime;
            var renewed       = ticket with { Properties = ticket.Properties with { ExpiresUtc = renewedExpiry < maximumExpiry ? renewedExpiry : maximumExpiry } };
            if (!await _store.RenewAsync(key, renewed, context.RequestAborted))
            {
                return AuthenticationResult.Fail("The session was revoked.");
            }

            ticket = renewed;
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

        var now     = _time.GetUtcNow();
        var issued  = properties.IssuedUtc  ?? now;
        var expires = properties.ExpiresUtc ?? now + _options.Lifetime;
        if (issued > now || issued + _options.MaximumLifetime <= now || expires <= now || expires > issued + _options.MaximumLifetime)
        {
            throw new ArgumentException("The session lifetime is invalid.", nameof(properties));
        }

        var items = new Dictionary<string, string>(properties.Items, StringComparer.Ordinal)
        {
            [CsrfProperty] = NewToken(),
        };
        var ticket = new AuthenticationTicket(principal, scheme, properties with { IssuedUtc = issued, ExpiresUtc = expires, Items = items }).Copy();
        var token  = NewToken();
        await _store.StoreAsync(Key(scheme, token), ticket, context.RequestAborted);
        // Store the replacement before revoking the old credential so a failed store leaves the old session usable.
        var previous = GetToken(context);
        if (previous is not null && ValidToken(previous))
        {
            await _store.RemoveAsync(Key(scheme, previous), context.RequestAborted);
        }

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
        if (token is not null && ValidToken(token))
        {
            await _store.RemoveAsync(Key(scheme, token), context.RequestAborted);
        }

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
        => context.Response.Cookies.Set(_options.CookieName, token, _options.Cookie with { Expires = properties.IsPersistent ? properties.ExpiresUtc : null });

    private static void EnsureWritable(RequestContext context)
    {
        if (context.Response.HasStarted)
        {
            throw new InvalidOperationException("Sign-in and sign-out must run before the response starts.");
        }
    }

    private static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool ValidToken(string token) => token.Length == 43 && token.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private static string Key(string scheme, string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scheme + "\0" + token)));
}
