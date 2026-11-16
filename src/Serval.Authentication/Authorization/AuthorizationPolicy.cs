#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.Extensions.DependencyInjection;
using System.Security.Claims;
using Serval.Middleware;

namespace Serval.Authentication.Authorization;

/// <summary>Checks one requirement against an existing principal.</summary>
/// <param name="context">The current request and its scoped services.</param>
/// <param name="principal">The principal to evaluate.</param>
/// <returns>True when the requirement passes; false to reject the policy.</returns>
public delegate ValueTask<bool> AuthorizationRequirement(RequestContext context, ClaimsPrincipal principal);

/// <summary>Combines an authentication requirement with checks that must all pass.</summary>
public sealed class AuthorizationPolicy
{
    /// <summary>Whether the principal must contain an authenticated identity. Defaults to true.</summary>
    public bool RequireAuthenticatedUser { get; set; } = true;

    /// <summary>Additional checks evaluated in order until one fails.</summary>
    public IList<AuthorizationRequirement> Requirements { get; } = new List<AuthorizationRequirement>();

    /// <summary>Adds a requirement that accepts any of the supplied roles.</summary>
    /// <param name="roles">One or more nonempty role names.</param>
    /// <returns>This policy for further configuration.</returns>
    /// <remarks>Separate calls add separate requirements, so each call must succeed.</remarks>
    /// <exception cref="ArgumentException">No role was supplied or a role is blank.</exception>
    public AuthorizationPolicy RequireRole(params string[] roles)
    {
        ArgumentNullException.ThrowIfNull(roles);

        var accepted = roles.ToArray();
        if (accepted.Length == 0 || accepted.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("At least one nonempty role is required.", nameof(roles));
        }

        Requirements.Add((_, user) => ValueTask.FromResult(accepted.Any(user.IsInRole)));
        return this;
    }

    /// <summary>Adds a requirement for a claim with an optional allowed-value list.</summary>
    /// <param name="type">The nonempty, case-sensitive claim type.</param>
    /// <param name="values">Allowed case-sensitive values; an empty list accepts any value of this claim type.</param>
    /// <returns>This policy for further configuration.</returns>
    public AuthorizationPolicy RequireClaim(string type, params string[] values)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        ArgumentNullException.ThrowIfNull(values);

        var accepted = values.ToArray();
        Requirements.Add((_, user) => ValueTask.FromResult(user.Claims.Any(claim => claim.Type == type && (accepted.Length == 0 || accepted.Contains(claim.Value, StringComparer.Ordinal)))));
        return this;
    }

    /// <summary>Adds an application-defined asynchronous requirement.</summary>
    /// <param name="requirement">The check to evaluate against the current request and principal.</param>
    /// <returns>This policy for further configuration.</returns>
    /// <exception cref="ArgumentNullException">The requirement is null.</exception>
    public AuthorizationPolicy Require(AuthorizationRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(requirement);

        Requirements.Add(requirement);
        return this;
    }
}

/// <summary>Checks policies and resource permissions without authenticating or writing a response.</summary>
public interface IAuthorizationService
{
    /// <summary>Evaluates the default or named policy against an existing principal.</summary>
    /// <param name="context">The current request and its scoped services.</param>
    /// <param name="principal">The principal to evaluate.</param>
    /// <param name="policy">A case-sensitive policy name, or null for the default policy.</param>
    /// <returns>True when authentication requirements and every policy check pass.</returns>
    /// <remarks>Does not authenticate or issue challenge/forbidden responses.</remarks>
    /// <exception cref="InvalidOperationException">The named policy is not registered.</exception>
    public ValueTask<bool> AuthorizeAsync(RequestContext context, ClaimsPrincipal principal, string? policy = null);

    // Existing policy-only implementations stay compatible. Resource checks must be
    // implemented explicitly instead of silently discarding the resource or permission.
    /// <summary>Evaluates an enum permission against a resource.</summary>
    /// <typeparam name="TPermission">The application's permission enum.</typeparam>
    /// <param name="context">The current request and its scoped services.</param>
    /// <param name="principal">The principal to evaluate.</param>
    /// <param name="resource">The resource being accessed, which may be null for an application-specific check.</param>
    /// <param name="permission">A declared enum value representing the operation.</param>
    /// <returns>The authorization decision and optional failure reason.</returns>
    /// <remarks>
    ///     The built-in service requires at least one grant and no denials. This interface's default implementation rejects
    ///     unsupported resource checks.
    /// </remarks>
    /// <exception cref="NotSupportedException">The implementation does not support resource permissions.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    ///     The built-in service receives an undeclared permission value.
    /// </exception>
    public ValueTask<AuthorizationResult> AuthorizeAsync<TPermission>(RequestContext context, ClaimsPrincipal principal, object? resource, TPermission permission) where TPermission : struct, Enum
        => ValueTask.FromException<AuthorizationResult>(new NotSupportedException("This authorization service does not support resource permissions."));
}

/// <summary>Evaluates policies and combines resource votes with denial taking precedence.</summary>
public sealed class AuthorizationService : IAuthorizationService
{
    private readonly AuthenticationOptions _options;
    private readonly IAuthorizationVoter[] _voters;

    /// <summary>Creates policy authorization without resource voters.</summary>
    /// <param name="options">The default and named policies.</param>
    /// <remarks>Resource checks return NoApplicableVoter until voters are supplied.</remarks>
    public AuthorizationService(AuthenticationOptions options) : this(options, []) {}

    /// <summary>Creates authorization using policies and a fixed snapshot of voters.</summary>
    /// <param name="options">The default and named policies.</param>
    /// <param name="voters">The voters to evaluate in registration order.</param>
    public AuthorizationService(AuthenticationOptions options, IEnumerable<IAuthorizationVoter> voters)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(voters);

        _options = options;
        _voters  = [.. voters];
    }

    /// <inheritdoc />
    public async ValueTask<bool> AuthorizeAsync(RequestContext context, ClaimsPrincipal principal, string? policy = null)
    {
        var selected = policy is null
            ? _options.DefaultPolicy
            : _options.Policies.TryGetValue(policy, out var named)
                ? named
                : throw new InvalidOperationException($"Authorization policy '{policy}' is not registered.");
        if (selected.RequireAuthenticatedUser && !principal.Identities.Any(identity => identity.IsAuthenticated))
        {
            return false;
        }

        foreach (var requirement in selected.Requirements)
        {
            context.RequestAborted.ThrowIfCancellationRequested();
            if (!await requirement(context, principal))
            {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc />
    public async ValueTask<AuthorizationResult> AuthorizeAsync<TPermission>(RequestContext context, ClaimsPrincipal principal, object? resource, TPermission permission) where TPermission : struct, Enum
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(principal);

        if (!Enum.IsDefined(permission))
        {
            throw new ArgumentOutOfRangeException(nameof(permission), "Use a declared permission enum value.");
        }

        context.RequestAborted.ThrowIfCancellationRequested();

        var  granted   = false;
        Enum operation = permission;
        foreach (var voter in _voters)
        {
            context.RequestAborted.ThrowIfCancellationRequested();

            var vote = await voter.VoteAsync(context, principal, resource, operation) ?? throw new InvalidOperationException($"Voter '{voter.GetType().Name}' returned no decision.");

            context.RequestAborted.ThrowIfCancellationRequested();

            if (vote.Decision == AuthorizationVoteDecision.Deny)
            {
                return AuthorizationResult.Fail(AuthorizationFailureKind.Denied, vote.Reason);
            }

            granted |= vote.Decision == AuthorizationVoteDecision.Grant;
        }

        // A grant never overrides a denial. Unknown resource/permission pairs fail closed.
        return granted
            ? AuthorizationResult.Success()
            : AuthorizationResult.Fail(AuthorizationFailureKind.NoApplicableVoter, "No voter granted this resource permission.");
    }
}

/// <summary>Populates the request principal and continues even when credentials are absent or rejected.</summary>
public sealed class AuthenticateAttribute : MiddlewareAttribute
{
    /// <summary>The authentication scheme, or null to use the configured default.</summary>
    public string? Scheme { get; set; }

    /// <inheritdoc />
    public override async ValueTask InvokeAsync(RequestContext context, RequestDelegate next)
    {
        await context.AuthenticateAsync(Scheme);
        await next(context);
    }
}

/// <summary>Authenticates and enforces a policy before allowing an endpoint to run.</summary>
/// <remarks>Failed authorization challenges an unauthenticated client and forbids an authenticated client.</remarks>
public sealed class AuthorizeAttribute : MiddlewareAttribute
{
    /// <summary>
    ///     The explicit scheme used to authenticate, challenge, and forbid, or null for operation defaults.
    /// </summary>
    public string? Scheme { get; set; }

    /// <summary>The policy name, or null for the default policy.</summary>
    public string? Policy { get; set; }

    /// <summary>An optional comma-separated list of roles. At least one must match in addition to the policy.</summary>
    public string? Roles { get; set; }

    /// <inheritdoc />
    public override async ValueTask InvokeAsync(RequestContext context, RequestDelegate next)
    {
        var result  = await context.AuthenticateAsync(Scheme);
        var allowed = await context.Services.GetRequiredService<IAuthorizationService>().AuthorizeAsync(context, context.User, Policy);
        if (Roles is not null)
        {
            allowed &= Roles.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Any(context.User.IsInRole);
        }

        if (allowed)
        {
            await next(context);
        }
        else if (result.Succeeded)
        {
            await context.ForbidAsync(Scheme);
        }
        else
        {
            await context.ChallengeAsync(Scheme);
        }
    }
}
