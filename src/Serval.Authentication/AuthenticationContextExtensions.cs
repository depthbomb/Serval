#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.Extensions.DependencyInjection;
using System.Security.Claims;

namespace Serval.Authentication;

/// <summary>Runs authentication operations using services from the current request.</summary>
public static class AuthenticationContextExtensions
{
    extension(RequestContext context)
    {
        /// <summary>Authenticates the request and updates its principal.</summary>
        /// <param name="scheme">An explicit scheme, or null for the authentication default.</param>
        /// <returns>The successful, missing, or failed authentication result.</returns>
        /// <remarks>
        ///     Results are cached per request and scheme. An unsuccessful result sets the current principal to anonymous.
        /// </remarks>
        /// <exception cref="InvalidOperationException">
        ///     Authentication services or a suitable scheme are not configured.
        /// </exception>
        public ValueTask<AuthenticationResult> AuthenticateAsync(string? scheme = null)
            => context.Services.GetRequiredService<IAuthenticationService>().AuthenticateAsync(context, scheme);

        /// <summary>Asks a handler to request credentials from the client.</summary>
        /// <param name="scheme">An explicit scheme, or null for the challenge default.</param>
        /// <param name="properties">Optional properties passed to the challenge handler.</param>
        /// <returns>An operation that completes when the challenge has been applied.</returns>
        /// <remarks>
        ///     Run before response startup. The selected handler determines whether to send a status, redirect, or another
        ///     challenge.
        /// </remarks>
        /// <exception cref="InvalidOperationException">
        ///     Authentication services or a suitable scheme are not configured.
        /// </exception>
        public ValueTask ChallengeAsync(string? scheme = null, AuthenticationProperties? properties = null)
            => context.Services.GetRequiredService<IAuthenticationService>().ChallengeAsync(context, scheme, properties);

        /// <summary>Asks a handler to deny access for an authenticated client.</summary>
        /// <param name="scheme">An explicit scheme, or null for the authentication default.</param>
        /// <returns>An operation that completes when access denial has been applied.</returns>
        /// <remarks>Run before response startup.</remarks>
        /// <exception cref="InvalidOperationException">
        ///     Authentication services or a suitable scheme are not configured.
        /// </exception>
        public ValueTask ForbidAsync(string? scheme = null)
            => context.Services.GetRequiredService<IAuthenticationService>().ForbidAsync(context, scheme);

        /// <summary>Creates a session and updates the request's authentication cache and principal.</summary>
        /// <param name="principal">A principal with at least one authenticated identity.</param>
        /// <param name="scheme">An explicit sign-in scheme, or null for the sign-in default.</param>
        /// <param name="properties">Optional lifetime, persistence, and application metadata.</param>
        /// <returns>An operation that completes when sign-in finishes.</returns>
        /// <remarks>The selected handler must support ISignInHandler. Run before response startup.</remarks>
        /// <exception cref="InvalidOperationException">
        ///     Authentication services or a suitable scheme are not configured.
        /// </exception>
        public ValueTask SignInAsync(ClaimsPrincipal principal, string? scheme = null, AuthenticationProperties? properties = null)
            => context.Services.GetRequiredService<IAuthenticationService>().SignInAsync(context, principal, scheme, properties);

        /// <summary>Revokes the selected session and makes the request principal anonymous.</summary>
        /// <param name="scheme">An explicit scheme, or null for the sign-in default.</param>
        /// <returns>An operation that completes when sign-out finishes.</returns>
        /// <remarks>The selected handler must support ISignInHandler. Run before response startup.</remarks>
        /// <exception cref="InvalidOperationException">
        ///     Authentication services or a suitable scheme are not configured.
        /// </exception>
        public ValueTask SignOutAsync(string? scheme = null)
            => context.Services.GetRequiredService<IAuthenticationService>().SignOutAsync(context, scheme);
    }
}
