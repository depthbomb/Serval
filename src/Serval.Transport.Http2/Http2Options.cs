#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using Serval.Hosting;

namespace Serval.Transport.Http2;

/// <summary>Configures the HTTP/2 TCP listener, TLS, and connection limits.</summary>
public sealed class Http2Options
{
    /// <summary>The TCP address to bind. Defaults to IPv4 loopback on port 8444.</summary>
    public IPEndPoint EndPoint { get; set; } = new(IPAddress.Loopback, 8444);

    /// <summary>
    ///     Whether connections use TLS with h2 ALPN. Defaults to true; false uses a cleartext HTTP/2 preface.
    /// </summary>
    public bool UseTls { get; set; } = true;

    /// <summary>
    ///     The server certificate with a private key, required when TLS is enabled. Keep it alive while the listener runs.
    /// </summary>
    public X509Certificate2? Certificate { get; set; }

    /// <summary>The positive concurrent connection limit. Defaults to 100.</summary>
    public int MaxConcurrentConnections { get; set; } = 100;

    // Includes peer resets and server resets caused by invalid or refused requests.
    /// <summary>
    ///     The positive per-connection reset limit per minute, counting peer resets and server resets. Defaults to 100.
    /// </summary>
    public int MaxStreamResetsPerMinute { get; set; } = 100;

    /// <summary>
    ///     The positive deadline for each TLS handshake and HTTP/2 preface/settings exchange. Defaults to ten seconds.
    /// </summary>
    public TimeSpan HandshakeTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>The positive deadline for a frame read or transport write. Defaults to two minutes.</summary>
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(2);

    internal void Validate()
    {
        if (UseTls && Certificate?.HasPrivateKey != true)
        {
            throw new ArgumentException("HTTP/2 over TLS requires a certificate with a private key.");
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxConcurrentConnections);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxStreamResetsPerMinute);
        ValidateTimeout(HandshakeTimeout, nameof(HandshakeTimeout));
        ValidateTimeout(IdleTimeout, nameof(IdleTimeout));
    }

    private static void ValidateTimeout(TimeSpan value, string name)
    {
        if (value <= TimeSpan.Zero || value.TotalMilliseconds > uint.MaxValue - 1)
        {
            throw new ArgumentOutOfRangeException(name);
        }
    }
}

/// <summary>Adds the HTTP/2 transport to a Serval host.</summary>
public static class Http2BuilderExtensions
{
    extension(ServalBuilder builder)
    {
        /// <summary>Validates and registers the HTTP/2 listener as a hosted service.</summary>
        /// <param name="configure">Configures the listener, certificate, and limits before registration.</param>
        /// <returns>The framework builder for further configuration.</returns>
        /// <exception cref="ArgumentException">
        ///     TLS is enabled without a certificate containing a private key.
        /// </exception>
        /// <exception cref="ArgumentOutOfRangeException">
        ///     A limit or timeout is outside its supported positive range.
        /// </exception>
        public ServalBuilder AddHttp2(Action<Http2Options> configure)
        {
            var options = new Http2Options();
            configure(options);
            options.Validate();
            builder.Services.AddSingleton(options);
            builder.Services.AddHostedService<Http2Server>();
            return builder;
        }
    }
}
