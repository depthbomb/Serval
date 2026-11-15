#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Security.Cryptography;
using System.Text;
using Serval.Authentication.Authorization;
using Serval.Authentication.Cookies;
using Serval.Authentication.Passwords;
using Serval.Hosting;

namespace Serval.Authentication;

/// <summary>Registers authentication schemes, account stores, and authorization voters.</summary>
public sealed class AuthenticationBuilder
{
    /// <summary>The underlying framework builder.</summary>
    public ServalBuilder Serval { get; }

    /// <summary>The host's service collection.</summary>
    public IServiceCollection Services => Serval.Services;

    /// <summary>Authentication defaults and policies configured before startup.</summary>
    public AuthenticationOptions Options { get; }

    internal AuthenticationBuilder(ServalBuilder serval, AuthenticationOptions options)
    {
        Serval  = serval;
        Options = options;
    }

    /// <summary>Registers a handler as a keyed, scoped authentication service.</summary>
    /// <typeparam name="THandler">The handler type activated through dependency injection.</typeparam>
    /// <param name="scheme">The nonempty, case-sensitive scheme name.</param>
    /// <returns>This builder for further configuration.</returns>
    /// <exception cref="ArgumentException">The scheme is empty or already registered.</exception>
    public AuthenticationBuilder AddScheme<THandler>(string scheme) where THandler : class, IAuthenticationHandler
        => AddScheme(scheme, services => ActivatorUtilities.CreateInstance<THandler>(services));

    /// <summary>Registers a scoped handler factory for an authentication scheme.</summary>
    /// <param name="scheme">The nonempty, case-sensitive scheme name.</param>
    /// <param name="factory">Creates the handler using services from the request scope.</param>
    /// <returns>This builder for further configuration.</returns>
    /// <exception cref="ArgumentException">The scheme is empty or already registered.</exception>
    /// <exception cref="ArgumentNullException">The factory is null.</exception>
    public AuthenticationBuilder AddScheme(string scheme, Func<IServiceProvider, IAuthenticationHandler> factory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scheme);
        ArgumentNullException.ThrowIfNull(factory);

        if (!Options.Schemes.Add(scheme))
        {
            throw new ArgumentException($"Authentication scheme '{scheme}' is already registered.", nameof(scheme));
        }

        Services.AddKeyedScoped<IAuthenticationHandler>(scheme, (services, _) => factory(services));
        return this;
    }

    /// <summary>Adds server-side cookie sessions with a default in-memory store.</summary>
    /// <param name="scheme">The scheme name, defaulting to Cookies.</param>
    /// <param name="configure">Optional cookie settings, applied before validation.</param>
    /// <returns>This builder for further configuration.</returns>
    /// <remarks>
    ///     The default cookie name is derived from the scheme. Register IAuthenticationSessionStore to replace
    ///     single-process storage. Secure cookies require HTTPS by default.
    /// </remarks>
    public AuthenticationBuilder AddCookie(string scheme = "Cookies", Action<CookieAuthenticationOptions>? configure = null)
    {
        var options = new CookieAuthenticationOptions
        {
            CookieName = "__Host-Serval." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scheme)))[..16],
        };
        configure?.Invoke(options);
        options.Validate();

        Services.TryAddSingleton(new MemoryAuthenticationSessionStoreOptions());
        Services.TryAddSingleton<IAuthenticationSessionStore, MemoryAuthenticationSessionStore>();

        return AddScheme(scheme, services => new CookieAuthenticationHandler(options, services.GetRequiredService<IAuthenticationSessionStore>(), services.GetRequiredService<TimeProvider>()));
    }

    /// <summary>Adds bearer-token authentication using an application validation callback.</summary>
    /// <param name="scheme">The nonempty, case-sensitive scheme name.</param>
    /// <param name="configure">Configures the required token validator and optional challenge behavior.</param>
    /// <returns>This builder for further configuration.</returns>
    /// <exception cref="ArgumentNullException">The callback or configured token validator is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The maximum token length is not positive.</exception>
    public AuthenticationBuilder AddBearer(string scheme, Action<BearerAuthenticationOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var options = new BearerAuthenticationOptions();
        configure(options);
        options.Validate();

        return AddScheme(scheme, _ => new BearerAuthenticationHandler(options));
    }

    /// <summary>Registers a scoped account store and password authenticator.</summary>
    /// <typeparam name="TStore">The application-owned password account store.</typeparam>
    /// <returns>This builder for further configuration.</returns>
    public AuthenticationBuilder AddPasswordAccounts<TStore>() where TStore : class, IPasswordAccountStore
    {
        Services.TryAddScoped<IPasswordAccountStore, TStore>();
        Services.TryAddSingleton<PasswordDummyHash>();
        Services.TryAddScoped(services => new PasswordAuthenticator(services.GetRequiredService<IPasswordAccountStore>(), services.GetRequiredService<IPasswordHasher>(), services.GetRequiredService<PasswordDummyHash>().Value));
        return this;
    }

    /// <summary>Registers a scoped authorization voter if its type is not already registered.</summary>
    /// <typeparam name="TVoter">The voter type, which may depend on scoped application services.</typeparam>
    /// <returns>This builder for further configuration.</returns>
    public AuthenticationBuilder AddVoter<TVoter>() where TVoter : class, IAuthorizationVoter
    {
        // Voters can depend on scoped application services; registering the same type twice is harmless.
        Services.TryAddEnumerable(ServiceDescriptor.Scoped<IAuthorizationVoter, TVoter>());
        return this;
    }
}

/// <summary>Adds authentication and authorization services to Serval.</summary>
public static class AuthenticationBuilderExtensions
{
    extension(ServalBuilder builder)
    {
        /// <summary>Registers authentication defaults, authorization, and the default password hasher.</summary>
        /// <param name="configure">An optional callback to configure defaults and named authorization policies.</param>
        /// <returns>A builder for registering authentication schemes and account stores.</returns>
        /// <exception cref="InvalidOperationException">Authentication was already registered.</exception>
        public AuthenticationBuilder AddAuthentication(Action<AuthenticationOptions>? configure = null)
        {
            if (builder.Services.Any(descriptor => descriptor.ServiceType == typeof(AuthenticationOptions)))
            {
                throw new InvalidOperationException("Authentication has already been registered.");
            }

            var options = new AuthenticationOptions();
            configure?.Invoke(options);
            builder.Services.AddSingleton(options);
            builder.Services.TryAddSingleton(TimeProvider.System);
            builder.Services.TryAddScoped<IAuthenticationService, AuthenticationService>();
            builder.Services.TryAddScoped<IAuthorizationService, AuthorizationService>();
            builder.Services.TryAddSingleton(new Pbkdf2PasswordHasherOptions());
            builder.Services.TryAddSingleton<IPasswordHasher, Pbkdf2PasswordHasher>();
            return new AuthenticationBuilder(builder, options);
        }
    }
}
