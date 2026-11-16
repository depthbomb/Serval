#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.Extensions.DependencyInjection;

namespace Serval.Middleware;

/// <summary>Runs one step of the request pipeline.</summary>
/// <param name="context">The current request and its scoped services.</param>
/// <returns>An operation covering this step and any awaited downstream work.</returns>
public delegate ValueTask RequestDelegate(RequestContext context);

/// <summary>Processes a request around the next step in the pipeline.</summary>
/// <remarks>
///     Middleware activated by UseMiddlewareAttribute is created per invocation. Types that own resources must implement
///     IAsyncDisposable. Cleanup runs after response callbacks and lifecycle completion hooks.
/// </remarks>
public interface IMiddleware
{
    /// <summary>Handles a request, optionally invoking the remaining pipeline.</summary>
    /// <param name="context">The current request and its scoped services.</param>
    /// <param name="next">
    ///     The next step; await it to surround downstream processing, or omit it to short-circuit.
    /// </param>
    /// <returns>An operation that completes when this middleware and its downstream work finish.</returns>
    ValueTask InvokeAsync(RequestContext context, RequestDelegate next);
}

/// <summary>Adds reusable attribute-based middleware to a controller or action.</summary>
/// <remarks>
///     Attribute instances are shared by concurrent requests. Store request-specific data in the context or local
///     variables.
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true, Inherited = false)]
public abstract class MiddlewareAttribute : Attribute
{
    /// <summary>The execution order. Lower values enter first; equal priorities keep registration order.</summary>
    public int Priority { get; set; }

    /// <summary>Creates middleware with the supplied execution priority.</summary>
    /// <param name="priority">The execution priority, defaulting to zero.</param>
    protected MiddlewareAttribute(int priority = 0)
    {
        Priority = priority;
    }

    /// <summary>Handles a request, optionally invoking the remaining pipeline.</summary>
    /// <param name="context">The current request and its scoped services.</param>
    /// <param name="next">The next step; omitting it short-circuits the endpoint.</param>
    /// <returns>An operation covering the middleware's work.</returns>
    public abstract ValueTask InvokeAsync(RequestContext context, RequestDelegate next);
}

/// <summary>Creates middleware from request services and explicit constructor arguments for each invocation.</summary>
public sealed class UseMiddlewareAttribute : MiddlewareAttribute
{
    private readonly Type[]?        _argumentTypes;
    private readonly object[]       _arguments;
    private readonly ObjectFactory? _factory;
    private readonly Type           _middlewareType;

    /// <summary>Configures the middleware type and its explicit constructor arguments.</summary>
    /// <param name="middlewareType">A concrete, closed class implementing IMiddleware.</param>
    /// <param name="arguments">
    ///     Constructor arguments supplied before resolving remaining dependencies from request services.
    /// </param>
    /// <exception cref="ArgumentNullException">The type or arguments array is null.</exception>
    /// <exception cref="ArgumentException">
    ///     The type is not valid middleware, or implements IDisposable without IAsyncDisposable.
    /// </exception>
    public UseMiddlewareAttribute(Type middlewareType, params object[] arguments)
    {
        ArgumentNullException.ThrowIfNull(middlewareType);
        ArgumentNullException.ThrowIfNull(arguments);
        if (!middlewareType.IsClass || middlewareType.IsAbstract || middlewareType.ContainsGenericParameters || !typeof(IMiddleware).IsAssignableFrom(middlewareType))
        {
            throw new ArgumentException("Middleware must be a concrete class implementing IMiddleware.", nameof(middlewareType));
        }

        if (typeof(IDisposable).IsAssignableFrom(middlewareType) && !typeof(IAsyncDisposable).IsAssignableFrom(middlewareType))
        {
            throw new ArgumentException("Middleware with cleanup must implement IAsyncDisposable. Synchronous disposal is not supported.", nameof(middlewareType));
        }

        _middlewareType = middlewareType;
        _arguments      = arguments;

        // Multiple constructors need DI's service-aware selection on each activation.
        // A single constructor has a fixed argument mapping that can be compiled once.
        if (middlewareType.GetConstructors().Length == 1 && arguments.All(argument => argument is not null))
        {
            _argumentTypes = [.. arguments.Select(argument => argument.GetType())];
            _factory       = ActivatorUtilities.CreateFactory(middlewareType, _argumentTypes);
        }
    }

    /// <inheritdoc />
    /// <remarks>The created middleware remains alive through response callbacks and is disposed before the request scope.</remarks>
    public override async ValueTask InvokeAsync(RequestContext context, RequestDelegate next)
    {
        var middleware = CreateMiddleware(context.Services);
        if (middleware is IAsyncDisposable disposable)
        {
            try
            {
                context.Response.RegisterForDisposeAsync(disposable);
            }
            catch
            {
                await disposable.DisposeAsync();
                throw;
            }
        }

        await middleware.InvokeAsync(context, next);
    }

    private IMiddleware CreateMiddleware(IServiceProvider services)
    {
        if (_factory is not null)
        {
            if (_arguments.Where((t, index) => t.GetType() != _argumentTypes![index]).Any())
            {
                return (IMiddleware)ActivatorUtilities.CreateInstance(services, _middlewareType, _arguments);
            }

            return (IMiddleware)_factory(services, _arguments);
        }

        return (IMiddleware)ActivatorUtilities.CreateInstance(services, _middlewareType, _arguments);
    }
}
