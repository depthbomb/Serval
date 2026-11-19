#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serval.Hosting;

namespace Serval.Transport.Kestrel;

/// <summary>Connects an ASP.NET Core host to Serval's request pipeline.</summary>
public static class KestrelBuilderExtensions
{
    extension(IApplicationBuilder application)
    {
        /// <summary>Enables WebSockets and dispatches all requests reaching this point through Serval.</summary>
        /// <param name="webSockets">Optional WebSocket settings, including allowed origins and keep-alive behavior.</param>
        /// <returns>The application builder.</returns>
        /// <remarks>
        ///     Register Serval through Services.AddServal before building the application. Configure listening addresses,
        ///     certificates, HTTP protocols, and server limits through Kestrel's standard hosting APIs. Serval's body,
        ///     header, concurrency, and request deadline limits also apply.
        ///     Application request services are scoped by Serval. Requests must observe cancellation during host shutdown.
        ///     Configure trusted proxies with Serval's forwarded-header middleware, without also applying ASP.NET Core
        ///     forwarded-header middleware. Connection metadata retains the socket endpoints.
        /// </remarks>
        public IApplicationBuilder UseServal(WebSocketOptions? webSockets = null)
        {
            ArgumentNullException.ThrowIfNull(application);

            var services = application.ApplicationServices;
            var middleware = new KestrelMiddleware(services.GetRequiredService<IRequestHandler>(), services.GetRequiredService<ServalOptions>(), services.GetRequiredService<IHostApplicationLifetime>());

            application.UseWebSockets(webSockets ?? new WebSocketOptions());
            application.Run(middleware.InvokeAsync);
            return application;
        }
    }
}
