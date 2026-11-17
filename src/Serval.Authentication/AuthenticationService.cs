#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.Extensions.DependencyInjection;
using System.Security.Claims;

namespace Serval.Authentication;

/// <summary>Resolves registered schemes and coordinates authentication for a request.</summary>
public interface IAuthenticationService
{
    /// <summary>Returns a cached scheme result without assigning RequestContext.User.</summary>
    /// <remarks>Handlers may still renew credentials. Implementations must explicitly support principal-independent authentication.</remarks>
    public ValueTask<AuthenticationResult> EvaluateAsync(RequestContext context, string? scheme = null)
        => ValueTask.FromException<AuthenticationResult>(new NotSupportedException("This authentication service does not support evaluation without principal assignment."));

    /// <summary>Authenticates the request and updates its principal.</summary>
    /// <param name="context">The current request and its scoped services.</param>
    /// <param name="scheme">An explicit scheme, or null for the authentication default.</param>
    /// <returns>The successful, missing, or failed authentication result.</returns>
    /// <remarks>
    ///     Results are cached per request and scheme. An unsuccessful result sets the current principal to anonymous.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    ///     No usable scheme is configured, or the selected handler cannot perform the operation.
    /// </exception>
    public ValueTask<AuthenticationResult> AuthenticateAsync(RequestContext context, string? scheme = null);

    /// <summary>Asks a handler to request credentials from the client.</summary>
    /// <param name="context">The current request and its scoped services.</param>
    /// <param name="scheme">An explicit scheme, or null for the challenge default.</param>
    /// <param name="properties">Optional properties passed to the challenge handler.</param>
    /// <returns>An operation that completes when the challenge has been applied.</returns>
    /// <remarks>
    ///     Run before response startup. The selected handler determines whether to send a status, redirect, or another
    ///     challenge.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    ///     No usable scheme is configured, or the selected handler cannot perform the operation.
    /// </exception>
    public ValueTask ChallengeAsync(RequestContext context, string? scheme = null, AuthenticationProperties? properties = null);

    /// <summary>Asks a handler to deny access for an authenticated client.</summary>
    /// <param name="context">The current request and its scoped services.</param>
    /// <param name="scheme">An explicit scheme, or null for the authentication default.</param>
    /// <returns>An operation that completes when access denial has been applied.</returns>
    /// <remarks>Run before response startup.</remarks>
    /// <exception cref="InvalidOperationException">
    ///     No usable scheme is configured, or the selected handler cannot perform the operation.
    /// </exception>
    public ValueTask ForbidAsync(RequestContext context, string? scheme = null);

    /// <summary>Creates a session and updates the request's authentication cache and principal.</summary>
    /// <param name="context">The current request and its scoped services.</param>
    /// <param name="principal">A principal with at least one authenticated identity.</param>
    /// <param name="scheme">An explicit sign-in scheme, or null for the sign-in default.</param>
    /// <param name="properties">Optional lifetime, persistence, and application metadata.</param>
    /// <returns>An operation that completes when sign-in finishes.</returns>
    /// <remarks>The selected handler must support ISignInHandler. Run before response startup.</remarks>
    /// <exception cref="InvalidOperationException">
    ///     No usable scheme is configured, or the selected handler cannot perform the operation.
    /// </exception>
    public ValueTask SignInAsync(RequestContext context, ClaimsPrincipal principal, string? scheme = null, AuthenticationProperties? properties = null);

    /// <summary>Revokes the selected session and makes the request principal anonymous.</summary>
    /// <param name="context">The current request and its scoped services.</param>
    /// <param name="scheme">An explicit scheme, or null for the sign-in default.</param>
    /// <returns>An operation that completes when sign-out finishes.</returns>
    /// <remarks>The selected handler must support ISignInHandler. Run before response startup.</remarks>
    /// <exception cref="InvalidOperationException">
    ///     No usable scheme is configured, or the selected handler cannot perform the operation.
    /// </exception>
    public ValueTask SignOutAsync(RequestContext context, string? scheme = null);
}

/// <summary>Uses scoped handlers and caches authentication results per request and scheme.</summary>
public sealed class AuthenticationService : IAuthenticationService
{
    private readonly AuthenticationOptions _options;

    private static readonly object CacheKey = new();

    /// <summary>Creates an authentication service using the configured schemes and defaults.</summary>
    /// <param name="options">The registered schemes and operation defaults.</param>
    public AuthenticationService(AuthenticationOptions options)
    {
        _options = options;
    }

    /// <inheritdoc />
    public async ValueTask<AuthenticationResult> AuthenticateAsync(RequestContext context, string? scheme = null)
    {
        var result = await EvaluateAsync(context, scheme);
        context.ApplyAuthentication(result);
        return result;
    }

    /// <inheritdoc />
    public async ValueTask<AuthenticationResult> EvaluateAsync(RequestContext context, string? scheme = null)
    {
        scheme = Resolve(scheme, _options.DefaultAuthenticateScheme);

        var cache = GetCache(context);
        if (!cache.TryGetValue(scheme, out var task))
        {
            // A handler's ValueTask may only be awaited once; the cached Task supports later checks safely.
            task = Handler(context, scheme).AuthenticateAsync(context, scheme).AsTask();
            cache.Add(scheme, task);
        }

        return await task;
    }

    /// <inheritdoc />
    public ValueTask ChallengeAsync(RequestContext context, string? scheme = null, AuthenticationProperties? properties = null)
    {
        scheme = Resolve(scheme, _options.DefaultChallengeScheme);
        return Handler(context, scheme).ChallengeAsync(context, scheme, properties ?? new AuthenticationProperties());
    }

    /// <inheritdoc />
    public ValueTask ForbidAsync(RequestContext context, string? scheme = null)
    {
        scheme = Resolve(scheme, _options.DefaultAuthenticateScheme);
        return Handler(context, scheme).ForbidAsync(context, scheme);
    }

    /// <inheritdoc />
    public async ValueTask SignInAsync(RequestContext            context,
                                       ClaimsPrincipal           principal,
                                       string?                   scheme     = null,
                                       AuthenticationProperties? properties = null)
    {
        ArgumentNullException.ThrowIfNull(principal);

        if (!principal.Identities.Any(identity => identity.IsAuthenticated))
        {
            throw new ArgumentException("An authenticated identity is required.", nameof(principal));
        }

        scheme = Resolve(scheme, _options.DefaultSignInScheme);

        var handler = Handler(context, scheme) as ISignInHandler ?? throw new InvalidOperationException($"Scheme '{scheme}' does not support sign-in.");
        var ticket  = await handler.SignInAsync(context, scheme, principal, properties ?? new AuthenticationProperties());
        var result  = AuthenticationResult.Success(ticket.Copy());

        GetCache(context)[scheme] = Task.FromResult(result);

        context.User = result.Ticket!.Copy().Principal;
    }

    /// <inheritdoc />
    public async ValueTask SignOutAsync(RequestContext context, string? scheme = null)
    {
        scheme = Resolve(scheme, _options.DefaultSignInScheme);

        var handler = Handler(context, scheme) as ISignInHandler ?? throw new InvalidOperationException($"Scheme '{scheme}' does not support sign-out.");
        await handler.SignOutAsync(context, scheme);

        GetCache(context)[scheme] = Task.FromResult(AuthenticationResult.Missing());

        context.User = new ClaimsPrincipal(new ClaimsIdentity());
    }

    private string Resolve(string? requested, string? operationDefault)
    {
        var scheme = requested ?? operationDefault ?? _options.DefaultScheme ?? throw new InvalidOperationException("Specify an authentication scheme or configure a default.");
        if (!_options.Schemes.Contains(scheme))
        {
            throw new InvalidOperationException($"Authentication scheme '{scheme}' is not registered.");
        }

        return scheme;
    }

    private static IAuthenticationHandler Handler(RequestContext context, string scheme)
        => context.Services.GetRequiredKeyedService<IAuthenticationHandler>(scheme);

    private static Dictionary<string, Task<AuthenticationResult>> GetCache(RequestContext context)
    {
        if (!context.Items.TryGetValue(CacheKey, out var cache))
        {
            cache = new Dictionary<string, Task<AuthenticationResult>>(StringComparer.Ordinal);
            context.Items.Add(CacheKey, cache);
        }

        return (Dictionary<string, Task<AuthenticationResult>>)cache!;
    }
}
