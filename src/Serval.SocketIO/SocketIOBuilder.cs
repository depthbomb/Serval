#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Serval.Hosting;
using Serval.SocketIO.Internal;

namespace Serval.SocketIO;

/// <summary>Registers fixed namespaces and their scoped handlers.</summary>
public sealed class SocketIOBuilder
{
    /// <summary>The underlying Serval builder.</summary>
    public ServalBuilder Serval { get; }

    private readonly Dictionary<string, Type> _handlers;

    internal SocketIOBuilder(ServalBuilder serval, Dictionary<string, Type> handlers)
    {
        Serval    = serval;
        _handlers = handlers;
    }

    /// <summary>Maps a case-sensitive namespace to an application handler. Use / for the default namespace.</summary>
    public SocketIOBuilder MapNamespace<THandler>(string name) where THandler : SocketIOHandler
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!name.StartsWith('/') || name.Length > 256 || name.Any(character => char.IsControl(character) || character is ',' or '?'))
        {
            throw new ArgumentException("Namespace names start with / and cannot contain commas, query strings, or control characters.", nameof(name));
        }

        _handlers.Add(name, typeof(THandler));
        Serval.Services.TryAddScoped<THandler>();
        return this;
    }
}

/// <summary>Registers the Socket.IO server and HTTP endpoints.</summary>
public static class SocketIOBuilderExtensions
{
    extension(ServalBuilder builder)
    {
        /// <summary>Adds Engine.IO 4 polling and WebSockets with the Socket.IO 5 default parser.</summary>
        /// <remarks>Register authentication middleware before these endpoints. Set EnableWebSockets to false on transports without upgrades.</remarks>
        public ServalBuilder AddSocketIO(Action<SocketIOBuilder> configure, Action<SocketIOOptions>? configureOptions = null)
        {
            ArgumentNullException.ThrowIfNull(configure);
            if (builder.Services.Any(service => service.ServiceType == typeof(SocketIOServer)))
            {
                throw new InvalidOperationException("Socket.IO has already been registered.");
            }

            var options = new SocketIOOptions();
            configureOptions?.Invoke(options);
            options.Validate();

            var handlers = new Dictionary<string, Type>(StringComparer.Ordinal);
            configure(new SocketIOBuilder(builder, handlers));
            builder.Services.AddSingleton(options);
            builder.Services.AddSingleton(new NamespaceRegistry(handlers));
            builder.Services.AddSingleton<SocketIOServer>();
            builder.Services.AddSingleton<IHostedService>(services => services.GetRequiredService<SocketIOServer>());
            builder.ConfigureEndpoints(endpoint =>
            {
                if (endpoint.ControllerType == typeof(SocketIOEndpoint))
                {
                    endpoint.RequestTimeout      = Timeout.InfiniteTimeSpan;
                    endpoint.MaxRequestBodyBytes = options.MaxPayloadBytes;
                }
            });
            foreach (var method in new[] { "GET", "POST", "OPTIONS" })
            {
                builder.AddController<SocketIOEndpoint>(method, options.Path, null, (endpoint, context) => endpoint.HandleAsync(context));
            }

            return builder;
        }
    }
}
