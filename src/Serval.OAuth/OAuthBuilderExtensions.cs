#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Serval.Authentication;
using Serval.Hosting;

namespace Serval.OAuth;

/// <summary>Registers named OAuth clients and browser sign-in schemes.</summary>
public static class OAuthBuilderExtensions
{
    private static void RegisterClient(IServiceCollection services, string name, OAuthClientOptions options)
    {
        if (services.Any(descriptor => descriptor.ServiceType == typeof(OAuthClient) && Equals(descriptor.ServiceKey, name)))
        {
            throw new ArgumentException($"OAuth client '{name}' is already registered.", nameof(name));
        }

        services.AddKeyedSingleton<OAuthClient>(name, (provider, _) => new OAuthClient(options, provider.GetService<TimeProvider>() ?? TimeProvider.System));
    }

    extension(AuthenticationBuilder builder)
    {
        /// <summary>Registers OIDC code-flow sign-in with discovery, PKCE, nonce, and ID-token validation.</summary>
        /// <param name="scheme">The unique provider scheme.</param>
        /// <param name="configure">Configures client credentials, callback URI, and OpenIdConnect.Issuer.</param>
        /// <returns>The authentication builder. Use BeginOAuthSignInAsync and CompleteOAuthSignInAsync for this scheme.</returns>
        public AuthenticationBuilder AddOpenIdConnect(string scheme, Action<OAuthOptions> configure)
        {
            ArgumentNullException.ThrowIfNull(configure);
            return builder.AddOAuth(scheme, options =>
            {
                options.OpenIdConnect = new OpenIdConnectOptions();
                configure(options);
                ArgumentNullException.ThrowIfNull(options.OpenIdConnect);
            });
        }

        /// <summary>Registers an OAuth provider, client, and default in-memory state store.</summary>
        /// <param name="scheme">The unique OAuth scheme name.</param>
        /// <param name="configure">
        ///     Configures endpoints, callback mapping, and the separate local sign-in scheme.
        /// </param>
        /// <returns>The authentication builder for further configuration.</returns>
        /// <remarks>Each provider needs a distinct callback URI. Register a local sign-in handler separately.</remarks>
        /// <exception cref="ArgumentException">
        ///     The configuration is invalid, a name or callback is reused, or the provider tries to sign in through itself.
        /// </exception>
        public AuthenticationBuilder AddOAuth(string scheme, Action<OAuthOptions> configure)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(scheme);
            ArgumentNullException.ThrowIfNull(configure);

            var options = new OAuthOptions();
            configure(options);
            options.Validate();

            if (scheme == options.SignInScheme)
            {
                throw new ArgumentException("OAuth must sign in through a separate local authentication scheme.");
            }

            var duplicateCallback = builder.Services.Any(descriptor => descriptor.ServiceType == typeof(OAuthOptions) && descriptor is { IsKeyedService: true, KeyedImplementationInstance: OAuthOptions registered } && registered.RedirectUri == options.RedirectUri);
            if (duplicateCallback)
            {
                throw new ArgumentException("Each OAuth provider must have a distinct callback URI to prevent provider mix-ups.");
            }

            if (builder.Services.Any(descriptor => descriptor.ServiceType == typeof(IAuthenticationHandler) && Equals(descriptor.ServiceKey, scheme)))
            {
                throw new ArgumentException($"Authentication scheme '{scheme}' is already registered.", nameof(scheme));
            }

            RegisterClient(builder.Services, scheme, options);

            builder.Services.AddKeyedSingleton(scheme, options);
            builder.Services.TryAddSingleton(new MemoryOAuthStateStoreOptions());
            builder.Services.TryAddSingleton<IOAuthStateStore, MemoryOAuthStateStore>();
            return builder.AddScheme(scheme, services => new OAuthAuthenticationHandler(options, services.GetRequiredKeyedService<OAuthClient>(scheme), services.GetRequiredService<IOAuthStateStore>(), services.GetRequiredService<TimeProvider>()));
        }
    }

    extension(ServalBuilder builder)
    {
        /// <summary>Registers a named singleton OAuth client without browser sign-in.</summary>
        /// <param name="name">The unique keyed-service name.</param>
        /// <param name="configure">Configures the client endpoints and credentials.</param>
        /// <returns>The framework builder for further configuration.</returns>
        /// <remarks>Resolve OAuthClient with the same service key.</remarks>
        /// <exception cref="ArgumentException">The name is reused or client configuration is invalid.</exception>
        public ServalBuilder AddOAuthClient(string name, Action<OAuthClientOptions> configure)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            ArgumentNullException.ThrowIfNull(configure);

            var options = new OAuthClientOptions();
            configure(options);
            options.Validate();

            RegisterClient(builder.Services, name, options);

            return builder;
        }
    }
}

/// <summary>Runs browser OAuth sign-in through a registered provider.</summary>
public static class OAuthContextExtensions
{
    private static OAuthAuthenticationHandler Handler(RequestContext context, string scheme)
        => context.Services.GetRequiredKeyedService<IAuthenticationHandler>(scheme) as OAuthAuthenticationHandler ?? throw new InvalidOperationException($"Scheme '{scheme}' is not an OAuth provider.");

    extension(RequestContext context)
    {
        /// <summary>Begins a browser authorization flow through the selected provider.</summary>
        /// <param name="scheme">The registered OAuth provider scheme.</param>
        /// <param name="returnUrl">The local return path starting with a single slash; defaults to /.</param>
        /// <param name="properties">Optional properties for the eventual local session.</param>
        /// <param name="parameters">Additional provider-specific authorization parameters.</param>
        /// <returns>An operation that completes when the authorization redirect is sent.</returns>
        /// <remarks>
        ///     Stores state and a browser-binding cookie, sets private response headers, and redirects with status 302.
        ///     Must run before response startup.
        /// </remarks>
        /// <exception cref="InvalidOperationException">
        ///     The selected scheme is not an OAuth provider or the response has already started.
        /// </exception>
        /// <exception cref="ArgumentException">The return path or provider parameters are invalid.</exception>
        public ValueTask BeginOAuthSignInAsync(string                               scheme,
                                               string                               returnUrl  = "/",
                                               AuthenticationProperties?            properties = null,
                                               IReadOnlyDictionary<string, string>? parameters = null)
            => Handler(context, scheme).BeginAsync(context, scheme, returnUrl, properties, parameters);

        /// <summary>Completes an OAuth callback and signs in through the provider's local scheme.</summary>
        /// <param name="scheme">The registered OAuth provider scheme.</param>
        /// <returns>The principal, tokens, and local return destination.</returns>
        /// <remarks>
        ///     Requires a GET to the configured callback URI. Consumes matching state once, validates the optional issuer,
        ///     exchanges the code, maps the principal, and signs in through the local scheme. The caller decides how to
        ///     redirect afterward.
        /// </remarks>
        /// <exception cref="OAuthProtocolException">The callback or provider response is invalid.</exception>
        /// <exception cref="InvalidOperationException">
        ///     The selected provider or local sign-in handler is unavailable, or the response has started.
        /// </exception>
        public ValueTask<OAuthSignInResult> CompleteOAuthSignInAsync(string scheme)
            => Handler(context, scheme).CompleteAsync(context, scheme);
    }
}
