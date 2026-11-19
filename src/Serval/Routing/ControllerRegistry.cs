#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.Extensions.DependencyInjection;
using Serval.Http;
using Serval.Middleware;

namespace Serval.Routing;

/// <summary>Invokes a controller action using request-scoped dependencies.</summary>
/// <typeparam name="TController">The controller type.</typeparam>
/// <param name="controller">The controller instance resolved from the request scope.</param>
/// <param name="context">The current request and its scoped services.</param>
/// <returns>The non-null result to execute against the response.</returns>
public delegate ValueTask<IActionResult> ControllerAction<in TController>(TController controller, RequestContext context) where TController : class;

/// <summary>Collects endpoints until the router freezes registrations at startup.</summary>
public sealed class ControllerRegistry
{
    private          bool                                _frozen;
    private readonly List<Func<ServalOptions, Endpoint>> _endpoints = [];

    /// <summary>Adds a delegate endpoint without activating a controller or executing an action result.</summary>
    public void Add(string                       method,
                    string                       template,
                    string?                      name,
                    RequestDelegate              handler,
                    params MiddlewareAttribute[] middleware)
        => Add(method, template, name, handler, middleware, null);

    /// <typeparam name="TController">
    ///     The controller type, which must already be registered with dependency injection.
    /// </typeparam>
    /// <param name="method">The HTTP method; registration normalizes it to uppercase.</param>
    /// <param name="template">The route template, including any parameter constraints.</param>
    /// <param name="name">A unique name for URL generation, or null for an unnamed route.</param>
    /// <param name="action">The action to run with a controller from the request scope.</param>
    /// <param name="middleware">Middleware applied in ascending priority order; ties keep registration order.</param>
    /// <remarks>
    ///     Templates use whole path segments: literals, {id}, {id:int}, optional {id?}, or defaults such as {page=1}.
    ///     Optional parameters and catch-alls must come last. {*path} escapes slashes when generating a URL;
    ///     {**path} preserves path separators. The router validates templates and duplicate routes at startup.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The router has frozen registrations.</exception>
    /// <exception cref="ArgumentException">The HTTP method is empty or whitespace.</exception>
    public void Add<TController>(string                        method,
                                 string                        template,
                                 string?                       name,
                                 ControllerAction<TController> action,
                                 params MiddlewareAttribute[]  middleware) where TController : class
        => Add(method, template, name, action, middleware, null);

    internal void Add(string                   method,
                      string                   template,
                      string?                  name,
                      RequestDelegate          handler,
                      MiddlewareAttribute[]    middleware,
                      Action<EndpointBuilder>? configure)
    {
        EnsureMutable();

        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(middleware);

        var attributes = middleware.ToArray();
        _endpoints.Add(options =>
        {
            var endpoint = new EndpointBuilder(method.ToUpperInvariant(), template, name, null, options);
            configure?.Invoke(endpoint);
            endpoint.Validate(options);
            return new Endpoint(endpoint, BuildPipeline(handler, endpoint.Middleware.Concat(attributes)));
        });
    }

    internal void Add<TController>(string                        method,
                                   string                        template,
                                   string?                       name,
                                   ControllerAction<TController> action,
                                   MiddlewareAttribute[]         middleware,
                                   Action<EndpointBuilder>?      configure) where TController : class
    {
        EnsureMutable();

        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(middleware);

        var attributes = middleware.ToArray();
        _endpoints.Add(options =>
        {
            var endpoint = new EndpointBuilder(method.ToUpperInvariant(), template, name, typeof(TController), options);
            configure?.Invoke(endpoint);
            endpoint.Validate(options);
            return new Endpoint(endpoint, BuildPipeline(action, endpoint.Middleware.Concat(attributes)));
        });
    }

    internal IReadOnlyList<Endpoint> Freeze(ServalOptions options)
    {
        _frozen = true;
        return [.. _endpoints.Select(create => create(options))];
    }

    internal void EnsureMutable()
    {
        if (_frozen)
        {
            throw new InvalidOperationException("Controller registration is closed after the host starts.");
        }
    }

    private static RequestDelegate BuildPipeline<TController>(ControllerAction<TController>    action,
                                                              IEnumerable<MiddlewareAttribute> middleware) where TController : class
    {
        return BuildPipeline(Pipeline, middleware);

        async ValueTask Pipeline(RequestContext context)
        {
            var controller     = context.Services.GetRequiredService<TController>();
            var controllerBase = controller as ControllerBase;
            controllerBase?.AttachContext(context);
            try
            {
                var result = await action(controller, context) ?? throw new InvalidOperationException("A controller action must return an IActionResult.");
                await result.ExecuteAsync(context);
            }
            finally
            {
                // Deferred results can use the controller too; keep its context until they finish.
                controllerBase?.DetachContext();
            }
        }
    }

    private static RequestDelegate BuildPipeline(RequestDelegate pipeline, IEnumerable<MiddlewareAttribute> middleware)
    {
        // Wrapping builds the pipeline inside out, so the lowest priority is added last and entered first.
        foreach (var attribute in middleware.OrderBy(attribute => attribute.Priority).Reverse())
        {
            var next = pipeline;
            pipeline = context => attribute.InvokeAsync(context, next);
        }

        return pipeline;
    }
}
