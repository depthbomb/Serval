#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.Extensions.DependencyInjection;

namespace Serval.Authentication.Authorization;

/// <summary>Checks permissions against the request's current principal.</summary>
public static class AuthorizationContextExtensions
{
    extension(RequestContext context)
    {
        // Checks the existing principal. Authentication and the response remain the caller's choice.
        /// <summary>Checks the current principal against a policy without changing the response.</summary>
        /// <param name="policy">A named policy, or null for the default.</param>
        /// <returns>True when all policy requirements pass.</returns>
        /// <remarks>Authenticate first when needed; this helper does not authenticate implicitly.</remarks>
        public ValueTask<bool> AuthorizeAsync(string? policy = null)
            => context.Services.GetRequiredService<IAuthorizationService>().AuthorizeAsync(context, context.User, policy);

        /// <summary>Checks a resource permission against the current principal.</summary>
        /// <typeparam name="TPermission">The application's permission enum.</typeparam>
        /// <param name="resource">The resource being accessed.</param>
        /// <param name="permission">A declared value of the permission enum.</param>
        /// <returns>The combined voter decision.</returns>
        /// <remarks>
        ///     The built-in service requires a grant and gives any denial precedence. It does not challenge the client or
        ///     modify the response.
        /// </remarks>
        public ValueTask<AuthorizationResult> AuthorizeAsync<TPermission>(object? resource, TPermission permission) where TPermission : struct, Enum
            => context.Services.GetRequiredService<IAuthorizationService>().AuthorizeAsync(context, context.User, resource, permission);
    }
}
