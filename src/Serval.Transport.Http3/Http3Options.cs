#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Quic;
using System.Security.Cryptography.X509Certificates;
using Serval.Hosting;

namespace Serval.Transport.Http3;

/// <summary>Configures the HTTP/3 QUIC listener and connection limits.</summary>
public sealed class Http3Options
{
    /// <summary>The QUIC UDP address to bind. Defaults to IPv4 loopback on port 8443.</summary>
    public IPEndPoint EndPoint { get; set; } = new(IPAddress.Loopback, 8443);

    /// <summary>The required TLS certificate with a private key. Keep it alive while the listener runs.</summary>
    public X509Certificate2? Certificate { get; set; }

    /// <summary>The positive concurrent QUIC connection limit. Defaults to 100.</summary>
    public int MaxConcurrentConnections { get; set; } = 100;

    /// <summary>The QUIC idle timeout passed to the listener. Defaults to two minutes.</summary>
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(2);
}

/// <summary>Adds the HTTP/3 transport to a Serval host.</summary>
public static class Http3BuilderExtensions
{
    extension(ServalBuilder builder)
    {
        /// <summary>Checks QUIC support and registers the HTTP/3 listener as a hosted service.</summary>
        /// <param name="configure">Configures the listener, certificate, and connection limits.</param>
        /// <returns>The framework builder for further configuration.</returns>
        /// <exception cref="PlatformNotSupportedException">
        ///     The platform or MsQuic installation does not support QuicListener.
        /// </exception>
        /// <exception cref="ArgumentException">No certificate containing a private key is supplied.</exception>
        /// <exception cref="ArgumentOutOfRangeException">The concurrent connection limit is not positive.</exception>
        public ServalBuilder AddHttp3(Action<Http3Options> configure)
        {
            if (!QuicListener.IsSupported)
            {
                throw new PlatformNotSupportedException("HTTP/3 requires a supported System.Net.Quic platform and MsQuic installation.");
            }

            var options = new Http3Options();
            configure(options);
            if (options.Certificate?.HasPrivateKey != true)
            {
                throw new ArgumentException("HTTP/3 requires a TLS certificate with a private key.", nameof(configure));
            }

            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxConcurrentConnections);
            builder.Services.AddSingleton(options);
            builder.Services.AddHostedService<Http3Server>();
            return builder;
        }
    }
}
