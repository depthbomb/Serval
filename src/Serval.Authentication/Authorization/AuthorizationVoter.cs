#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using System.Security.Claims;

namespace Serval.Authentication.Authorization;

/// <summary>Decides resource permissions within one application's area of responsibility.</summary>
public interface IAuthorizationVoter
{
    /// <summary>Evaluates a resource permission or abstains when it does not apply.</summary>
    /// <param name="context">The current request and its scoped services.</param>
    /// <param name="principal">The principal requesting access.</param>
    /// <param name="resource">The resource being accessed.</param>
    /// <param name="permission">The requested enum permission.</param>
    /// <returns>A non-null grant, denial, or abstention.</returns>
    /// <remarks>
    ///     Use context.RequestAborted for asynchronous work. Abstain for resource or permission types outside this voter's
    ///     responsibility.
    /// </remarks>
    public ValueTask<AuthorizationVote> VoteAsync(RequestContext context, ClaimsPrincipal principal, object? resource, Enum permission);
}

/// <summary>Routes matching resource and permission types to a typed voter.</summary>
/// <typeparam name="TResource">The supported resource type.</typeparam>
/// <typeparam name="TPermission">The supported permission enum.</typeparam>
/// <remarks>
///     The public dispatcher abstains for mismatched types, null resources, or checks rejected by Supports.
/// </remarks>
public abstract class AuthorizationVoter<TResource, TPermission> : IAuthorizationVoter where TPermission : struct, Enum
{
    /// <summary>Evaluates the principal's access to a supported resource and permission.</summary>
    /// <param name="context">The current request and its scoped services.</param>
    /// <param name="principal">The principal requesting access.</param>
    /// <param name="resource">The resource being accessed.</param>
    /// <param name="permission">The requested enum permission.</param>
    /// <returns>A non-null grant, denial, or abstention.</returns>
    /// <remarks>
    ///     The public dispatcher checks types and Supports before invoking the protected overload. Implement
    ///     request-specific authorization in the protected overload.
    /// </remarks>
    public ValueTask<AuthorizationVote> VoteAsync(RequestContext context, ClaimsPrincipal principal, object? resource, Enum permission)
    {
        if (resource is not TResource subject || permission is not TPermission operation || !Supports(subject, operation))
        {
            return ValueTask.FromResult(AuthorizationVote.Abstain());
        }

        return VoteAsync(context, principal, subject, operation);
    }

    /// <summary>Selects the resource and permission pairs this voter handles.</summary>
    /// <param name="resource">The typed resource.</param>
    /// <param name="permission">The typed permission.</param>
    /// <returns>True to invoke the typed vote method; false to abstain.</returns>
    /// <remarks>
    ///     Defaults to true. Keep user-specific decisions in VoteAsync, which receives the current principal.
    /// </remarks>
    protected virtual bool Supports(TResource resource, TPermission permission) => true;

    /// <summary>Evaluates the principal's access to a supported resource and permission.</summary>
    /// <param name="context">The current request and its scoped services.</param>
    /// <param name="principal">The principal requesting access.</param>
    /// <param name="resource">The resource being accessed.</param>
    /// <param name="permission">The requested enum permission.</param>
    /// <returns>A non-null grant, denial, or abstention.</returns>
    /// <remarks>
    ///     The public dispatcher checks types and Supports before invoking the protected overload. Implement
    ///     request-specific authorization in the protected overload.
    /// </remarks>
    protected abstract ValueTask<AuthorizationVote> VoteAsync(RequestContext context, ClaimsPrincipal principal, TResource resource, TPermission permission);
}
