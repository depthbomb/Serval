#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using System.Text.Json.Serialization;
using Serval.Http.StaticFiles;
using Serval.Lifecycle;
using Serval.Middleware;
using Serval.Routing;
using Serval.Validation;

namespace Serval.Hosting;

/// <summary>Configures framework services, options, and controller endpoints before startup.</summary>
public sealed class ServalBuilder
{
    /// <summary>The service collection used by the host and request scopes.</summary>
    public IServiceCollection Services { get; }

    /// <summary>The endpoint registry populated before the router is created.</summary>
    public ControllerRegistry Controllers { get; }

    /// <summary>The shared framework options. Configure them before starting the host.</summary>
    public ServalOptions Options { get; }

    private readonly ApplicationPipeline           _pipeline;
    private readonly RequestLifecycleRegistry      _lifecycle;
    private readonly List<Action<EndpointBuilder>> _conventions = [];
    private readonly Action<EndpointBuilder>?      _parentConvention;
    private readonly string                        _prefix;
    private readonly string                        _namePrefix;

    internal ServalBuilder(IServiceCollection       services,
                           ControllerRegistry       controllers,
                           ServalOptions            options,
                           ApplicationPipeline      pipeline,
                           RequestLifecycleRegistry lifecycle,
                           string                   prefix           = "",
                           string                   namePrefix       = "",
                           Action<EndpointBuilder>? parentConvention = null)
    {
        Services          = services;
        Controllers       = controllers;
        Options           = options;
        _pipeline         = pipeline;
        _lifecycle        = lifecycle;
        _prefix           = prefix;
        _namePrefix       = namePrefix;
        _parentConvention = parentConvention;
    }

    /// <summary>Configures every endpoint registered through this builder or its groups when routes are frozen.</summary>
    /// <remarks>Parent conventions run first, then group conventions, regardless of when controllers were added.</remarks>
    public ServalBuilder ConfigureEndpoints(Action<EndpointBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        Controllers.EnsureMutable();
        _conventions.Add(configure);
        return this;
    }

    /// <summary>Adds a scoped input validator. Use InputValidation or FromBody(Validate = true) to run it.</summary>
    public ServalBuilder AddInputValidator<TInput, TValidator>() where TValidator : class, IInputValidator<TInput>
    {
        Services.TryAddEnumerable(ServiceDescriptor.Scoped<IInputValidator<TInput>, TValidator>());
        return this;
    }

    /// <summary>Registers controllers under a shared prefix and endpoint conventions.</summary>
    /// <remarks>
    ///     Groups can nest. An optional name prefix also scopes URL-generation names. Use still registers
    ///     application-wide middleware.
    /// </remarks>
    public ServalBuilder AddGroup(string prefix, Action<ServalBuilder> register, string? namePrefix = null)
    {
        ArgumentNullException.ThrowIfNull(register);
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        Controllers.EnsureMutable();
        if (!prefix.StartsWith('/') || prefix.StartsWith("//", StringComparison.Ordinal) || prefix.Contains('?') || prefix.Contains('#'))
        {
            throw new ArgumentException("A group prefix must be an application-relative route path.", nameof(prefix));
        }

        var group = new ServalBuilder(Services, Controllers, Options, _pipeline, _lifecycle, _prefix + prefix.TrimEnd('/'), _namePrefix + namePrefix, ConfigureEndpoint);
        register(group);
        return this;
    }

    /// <summary>Adds application middleware in registration order, covering routes, files, redirects, and errors.</summary>
    /// <remarks>Await next to continue. Use response callbacks to observe completion or modify final error headers.</remarks>
    public ServalBuilder Use(Func<RequestContext, RequestDelegate, ValueTask> middleware)
    {
        _pipeline.Add(middleware);
        return this;
    }

    /// <summary>Replaces default routing, static files, redirects, and not-found handling with an application dispatcher.</summary>
    /// <remarks>Request scopes, framing checks, cancellation, exception handling, and response cleanup still apply. Applies application-wide.</remarks>
    public ServalBuilder DispatchWith(RequestDelegate dispatcher)
    {
        _pipeline.SetTerminal(dispatcher);
        return this;
    }

    /// <summary>Registers a delegate endpoint with the same groups, metadata, limits, and middleware as controllers.</summary>
    public ServalBuilder AddEndpoint(string method, string template, RequestDelegate handler, string? name = null, params MiddlewareAttribute[] middleware)
    {
        var path = _prefix.Length == 0 ? template : _prefix             + "/" + template.TrimStart('/');
        Controllers.Add(method, path, name is null ? null : _namePrefix + name, handler, middleware, ConfigureEndpoint);
        return this;
    }

    /// <summary>Activates application middleware per request with constructor injection and asynchronous disposal.</summary>
    public ServalBuilder UseMiddleware<TMiddleware>(params object[] arguments) where TMiddleware : class, IMiddleware
    {
        var middleware = new UseMiddlewareAttribute(typeof(TMiddleware), arguments);
        return Use(middleware.InvokeAsync);
    }

    /// <summary>Adds a request-scoped handler for receiving requests, starting responses, and observing completion.</summary>
    /// <param name="priority">Lower values run first in every phase; ties keep registration order. Defaults to zero.</param>
    /// <typeparam name="THandler">A concrete handler registered once, with scoped constructor dependencies.</typeparam>
    /// <remarks>
    ///     Registration applies application-wide, including when called on a group. Local response callbacks run before
    ///     lifecycle hooks. Use middleware for wrapping or short-circuiting downstream work.
    /// </remarks>
    public ServalBuilder AddRequestLifecycleHandler<THandler>(int priority = 0) where THandler : RequestLifecycleHandler
    {
        _lifecycle.Add<THandler>(priority);
        return this;
    }

    /// <summary>
    ///     Interprets forwarded request information from explicitly trusted proxies. Register before middleware that uses
    ///     it.
    /// </summary>
    public ServalBuilder UseForwardedHeaders(Action<ForwardedHeadersOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var options = new ForwardedHeadersOptions();
        configure(options);
        var middleware = new ForwardedHeadersMiddleware(options);
        return Use(middleware.InvokeAsync);
    }

    /// <summary>Adds generated JSON metadata for request bodies and JSON results.</summary>
    /// <param name="context">An application's source-generated serialization context, usually its Default instance.</param>
    /// <returns>This builder for further configuration.</returns>
    /// <remarks>
    ///     Contexts are checked in registration order, after any resolvers already configured on Options.Json.
    ///     The first resolver supporting a type wins. Register before the host starts. Configured JSON options still apply;
    ///     context-specific settings require configuring those options or passing explicit JsonTypeInfo metadata.
    ///     Adding a context does not add a reflection fallback for types absent from every registered resolver.
    ///     Explicit metadata passed to a request or result takes precedence over this registration.
    /// </remarks>
    /// <exception cref="ArgumentNullException">The context is null.</exception>
    /// <exception cref="InvalidOperationException">The JSON options are already read-only.</exception>
    public ServalBuilder AddJsonContext(JsonSerializerContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        Options.Json.TypeInfoResolverChain.Add(context);
        return this;
    }

    /// <summary>Registers a controller as a scoped service and adds one endpoint.</summary>
    /// <typeparam name="TController">The controller type resolved for the request.</typeparam>
    /// <param name="method">The HTTP method; registration normalizes it to uppercase.</param>
    /// <param name="template">The route template, including any parameter constraints.</param>
    /// <param name="name">A unique name for URL generation, or null for an unnamed route.</param>
    /// <param name="action">The action to run with a controller from the request scope.</param>
    /// <param name="middleware">Middleware applied in ascending priority order; ties keep registration order.</param>
    /// <returns>This builder for further configuration.</returns>
    /// <exception cref="InvalidOperationException">The controller registry has already been frozen.</exception>
    /// <exception cref="ArgumentException">The HTTP method is empty or whitespace.</exception>
    public ServalBuilder AddController<TController>(string                        method,
                                                    string                        template,
                                                    string?                       name,
                                                    ControllerAction<TController> action,
                                                    params MiddlewareAttribute[]  middleware) where TController : class
    {
        Services.TryAddScoped<TController>();
        var path = _prefix.Length == 0 ? template : _prefix             + "/" + template.TrimStart('/');
        Controllers.Add(method, path, name is null ? null : _namePrefix + name, action, middleware, ConfigureEndpoint);
        return this;
    }

    private void ConfigureEndpoint(EndpointBuilder endpoint)
    {
        _parentConvention?.Invoke(endpoint);
        foreach (var configure in _conventions)
        {
            configure(endpoint);
        }
    }
}

/// <summary>Registers Serval with a host's service collection.</summary>
public static class ServalServiceCollectionExtensions
{
    extension(IServiceCollection services)
    {
        /// <summary>Registers the request pipeline, routing, and default error handling.</summary>
        /// <param name="configure">An optional callback run before the options are registered.</param>
        /// <returns>A builder for adding controllers, middleware, and transports.</returns>
        /// <remarks>Register a transport separately. Options and routes are validated when the host starts.</remarks>
        /// <exception cref="InvalidOperationException">
        ///     Serval was already registered in this service collection.
        /// </exception>
        public ServalBuilder AddServal(Action<ServalOptions>? configure = null)
        {
            if (services.Any(descriptor => descriptor.ServiceType == typeof(ServalOptions)))
            {
                throw new InvalidOperationException("Serval has already been registered in this service collection.");
            }

            var options = new ServalOptions();
            configure?.Invoke(options);

            var registry  = new ControllerRegistry();
            var pipeline  = new ApplicationPipeline();
            var lifecycle = new RequestLifecycleRegistry(services);

            services.AddSingleton(options);
            services.AddSingleton(registry);
            services.AddSingleton(pipeline);
            services.AddSingleton(lifecycle);
            services.AddSingleton<Router>();
            services.AddSingleton<IEndpointMatcher>(provider => provider.GetRequiredService<Router>());
            services.AddSingleton<StaticFileServer>();
            services.AddSingleton<IUrlGenerator>(provider => provider.GetRequiredService<Router>());
            services.TryAddSingleton<IRequestExceptionHandler, DefaultRequestExceptionHandler>();
            services.AddSingleton<IRequestHandler, RequestHandler>();
            services.AddHostedService<RouteValidationService>();

            return new ServalBuilder(services, registry, options, pipeline, lifecycle);
        }
    }

    private sealed class RouteValidationService : IHostedService
    {
        private readonly Router          _router;
        private readonly IRequestHandler _handler;

        public RouteValidationService(Router router, IRequestHandler handler)
        {
            _router  = router;
            _handler = handler;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            // Resolving the router here catches bad routes at startup, before the first request.
            _ = _router;
            _ = _handler;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
