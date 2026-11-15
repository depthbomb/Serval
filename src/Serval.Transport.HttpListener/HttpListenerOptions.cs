#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.Extensions.DependencyInjection;
using Serval.Hosting;

namespace Serval.Transport.HttpListener;

/// <summary>Configures the platform HttpListener transport.</summary>
public sealed class HttpListenerOptions
{
    /// <summary>
    ///     The listener URL prefixes, initially containing http://localhost:8080/. Clear this list to replace that default.
    /// </summary>
    /// <remarks>
    ///     Prefixes use HttpListener syntax and must end with a slash. Host startup applies the platform's binding and
    ///     URL-reservation rules.
    /// </remarks>
    public List<string> Prefixes { get; } = ["http://localhost:8080/"];
}

/// <summary>Adds the platform HttpListener transport to a Serval host.</summary>
public static class HttpListenerBuilderExtensions
{
    extension(ServalBuilder builder)
    {
        /// <summary>Registers an HttpListener server as a hosted service.</summary>
        /// <param name="configure">An optional callback to configure listener prefixes.</param>
        /// <returns>The framework builder for further configuration.</returns>
        /// <exception cref="ArgumentException">No listener prefixes remain after configuration.</exception>
        public ServalBuilder AddHttpListener(Action<HttpListenerOptions>? configure = null)
        {
            var options = new HttpListenerOptions();
            configure?.Invoke(options);
            if (options.Prefixes.Count == 0)
            {
                throw new ArgumentException("At least one HTTP listener prefix is required.");
            }

            builder.Services.AddSingleton(options);
            builder.Services.AddHostedService<HttpListenerServer>();
            return builder;
        }
    }
}
