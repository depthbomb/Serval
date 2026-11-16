#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using System.Security.Claims;
using Serval.Http.Constants;

namespace Serval.Authentication;

/// <summary>Handles authentication, challenges, and access denial for a single scheme.</summary>
public interface IAuthenticationHandler
{
    /// <summary>Reads and validates credentials without issuing a challenge.</summary>
    /// <param name="context">The current request and its scoped services.</param>
    /// <param name="scheme">The scheme under which this handler is registered.</param>
    /// <returns>A successful ticket, missing-credentials result, or failure.</returns>
    /// <remarks>
    ///     Use context.RequestAborted for asynchronous work. AuthenticationService applies the result to the current
    ///     principal.
    /// </remarks>
    public ValueTask<AuthenticationResult> AuthenticateAsync(RequestContext context, string scheme);

    /// <summary>Requests credentials from an unauthenticated client.</summary>
    /// <param name="context">The current request and its scoped services.</param>
    /// <param name="scheme">The selected authentication scheme.</param>
    /// <param name="properties">Properties supplied by the caller for this challenge.</param>
    /// <returns>An operation that completes when the challenge is applied.</returns>
    /// <remarks>The response must still be writable.</remarks>
    public ValueTask ChallengeAsync(RequestContext context, string scheme, AuthenticationProperties properties);

    /// <summary>Denies access to an authenticated client.</summary>
    /// <param name="context">The current request and its scoped services.</param>
    /// <param name="scheme">The selected authentication scheme.</param>
    /// <returns>An operation that completes when the denial is applied.</returns>
    public ValueTask ForbidAsync(RequestContext context, string scheme);
}

/// <summary>Extends a scheme with session creation and revocation.</summary>
public interface ISignInHandler : IAuthenticationHandler
{
    /// <summary>Creates credentials for an authenticated principal.</summary>
    /// <param name="context">The current request and its scoped services.</param>
    /// <param name="scheme">The scheme issuing the credentials.</param>
    /// <param name="principal">The authenticated principal to store.</param>
    /// <param name="properties">Requested lifetime, persistence, and application metadata.</param>
    /// <returns>The issued ticket, including any handler-supplied properties.</returns>
    /// <remarks>Run before response startup and honor context.RequestAborted.</remarks>
    public ValueTask<AuthenticationTicket> SignInAsync(RequestContext           context,
                                                       string                   scheme,
                                                       ClaimsPrincipal          principal,
                                                       AuthenticationProperties properties);

    /// <summary>Revokes the request's credentials and removes any associated client cookie.</summary>
    /// <param name="context">The current request and its scoped services.</param>
    /// <param name="scheme">The scheme whose credentials should be revoked.</param>
    /// <returns>An operation that completes when sign-out finishes.</returns>
    public ValueTask SignOutAsync(RequestContext context, string scheme);
}

/// <summary>Provides default 401 challenges and 403 access-denied responses.</summary>
public abstract class AuthenticationHandler : IAuthenticationHandler
{
    /// <inheritdoc />
    public abstract ValueTask<AuthenticationResult> AuthenticateAsync(RequestContext context, string scheme);

    /// <inheritdoc />
    /// <remarks>The base implementation sets status 401 without starting the response.</remarks>
    public virtual ValueTask ChallengeAsync(RequestContext context, string scheme, AuthenticationProperties properties)
    {
        context.Response.StatusCode = StatusCodes.Unauthorized;
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>The base implementation sets status 403 without starting the response.</remarks>
    public virtual ValueTask ForbidAsync(RequestContext context, string scheme)
    {
        context.Response.StatusCode = StatusCodes.Forbidden;
        return ValueTask.CompletedTask;
    }
}
