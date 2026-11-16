#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Serval.Hosting;
using Serval.Routing;

namespace Serval.SignedUrls;

/// <summary>Registers signed URL generation and validation with Serval.</summary>
public static class SignedUrlBuilderExtensions
{
    extension(ServalBuilder builder)
    {
        /// <summary>Registers a singleton signer as both ISignedUrlService and SignedUrlService.</summary>
        /// <param name="configure">Configures the required secret signing key and optional settings.</param>
        /// <returns>The framework builder for further configuration.</returns>
        /// <remarks>Snapshots configuration immediately and respects an existing TimeProvider registration.</remarks>
        /// <exception cref="ArgumentException">The signing configuration is invalid.</exception>
        /// <exception cref="InvalidOperationException">A signed URL service is already registered.</exception>
        public ServalBuilder AddSignedUrls(Action<SignedUrlOptions> configure)
        {
            ArgumentNullException.ThrowIfNull(configure);

            if (builder.Services.Any(descriptor => descriptor.ServiceType == typeof(ISignedUrlService) || descriptor.ServiceType == typeof(SignedUrlService)))
            {
                throw new InvalidOperationException("Signed URLs have already been registered.");
            }

            var options = new SignedUrlOptions();
            configure(options);
            options.Validate();

            var snapshot = new SignedUrlOptions
            {
                SigningKey             = [.. options.SigningKey],
                SignatureParameterName = options.SignatureParameterName,
                MaxUrlLength           = options.MaxUrlLength,
            };

            builder.Services.TryAddSingleton(TimeProvider.System);
            builder.Services.AddSingleton(services => new SignedUrlService(services.GetRequiredService<IUrlGenerator>(), snapshot, services.GetRequiredService<TimeProvider>()));
            builder.Services.AddSingleton<ISignedUrlService>(services => services.GetRequiredService<SignedUrlService>());
            return builder;
        }
    }
}
