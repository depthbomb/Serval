#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Globalization;
using Serval.Routing;

namespace Serval.Savanna;

/// <summary>Renders an application component into the current Savanna output.</summary>
/// <typeparam name="TArguments">The component's input type.</typeparam>
public interface ISavannaComponent<in TArguments>
{
    /// <summary>Writes the component's markup using the supplied render context.</summary>
    /// <param name="writer">The shared writer; leave it open.</param>
    /// <param name="arguments">The component inputs.</param>
    /// <param name="context">The services, formatting, cancellation, and slots for this render.</param>
    /// <returns>An operation that completes when component rendering finishes.</returns>
    /// <remarks>
    ///     Await writes in order and honor context.CancellationToken. The caller owns the writer and flushes it.
    /// </remarks>
    ValueTask RenderAsync(SavannaWriter writer, TArguments arguments, SavannaRenderContext context);
}

/// <summary>Supplies services and settings shared by a view and its components.</summary>
public sealed class SavannaRenderContext
{
    /// <summary>The optional provider used for component and URL-service resolution.</summary>
    public IServiceProvider? Services { get; }

    /// <summary>The token observed by view, fragment, and writer operations.</summary>
    public CancellationToken CancellationToken { get; }

    /// <summary>The formatter for dynamic values, captured from the current culture when not supplied.</summary>
    public IFormatProvider FormatProvider { get; }

    /// <summary>Whether the render uses the development environment; this does not control compiled comments.</summary>
    public bool IsDevelopment { get; }

    /// <summary>The optional policy applied before writing dynamic URLs.</summary>
    public IUrlPolicy? UrlPolicy { get; }

    /// <summary>Resolves the application's named-route URL generator.</summary>
    /// <exception cref="InvalidOperationException">No IUrlGenerator service is available.</exception>
    public IUrlGenerator Urls => Services?.GetService<IUrlGenerator>()
                                 ?? throw new InvalidOperationException("URL generation requires Serval's IUrlGenerator service.");

    /// <summary>The named fragments available to this render, or the shared empty set.</summary>
    public SavannaSlots Slots { get; }

    /// <summary>Creates a render context, resolving optional settings from services and the current culture.</summary>
    /// <param name="services">Optional services for components, URL generation, and environment settings.</param>
    /// <param name="cancellationToken">The cancellation token for all rendering work.</param>
    /// <param name="formatProvider">The value formatter, or null to capture the current culture.</param>
    /// <param name="development">
    ///     The environment flag exposed to template code; null checks IHostEnvironment, then defaults to false.
    ///     Generated comment detail is controlled separately by the template project's DEBUG symbol.
    /// </param>
    /// <param name="urlPolicy">An optional URL policy; null resolves IUrlPolicy from services when available.</param>
    /// <param name="slots">Named render fragments, or null for an empty set.</param>
    public SavannaRenderContext(IServiceProvider? services          = null,
                                CancellationToken cancellationToken = default,
                                IFormatProvider?  formatProvider    = null,
                                bool?             development       = null,
                                IUrlPolicy?       urlPolicy         = null,
                                SavannaSlots?     slots             = null)
    {
        Services          = services;
        CancellationToken = cancellationToken;
        FormatProvider    = formatProvider ?? CultureInfo.CurrentCulture;
        IsDevelopment     = development    ?? services?.GetService<IHostEnvironment>()?.IsDevelopment() ?? false;
        UrlPolicy         = urlPolicy      ?? services?.GetService<IUrlPolicy>();
        Slots             = slots          ?? SavannaSlots.Empty;
    }

    /// <summary>Creates a context with different slots and the same services and settings.</summary>
    /// <param name="slots">The replacement slot set.</param>
    /// <returns>This context if the slot instance is unchanged; otherwise a new context.</returns>
    /// <exception cref="ArgumentNullException">The slot set is null.</exception>
    public SavannaRenderContext WithSlots(SavannaSlots slots)
    {
        ArgumentNullException.ThrowIfNull(slots);

        return ReferenceEquals(slots, Slots) ? this : new SavannaRenderContext(Services, CancellationToken, FormatProvider, IsDevelopment, UrlPolicy, slots);
    }

    /// <summary>Resolves a component from the render's service provider.</summary>
    /// <typeparam name="TComponent">The service type to resolve.</typeparam>
    /// <returns>The registered component instance.</returns>
    /// <remarks>The service provider owns the component's lifetime.</remarks>
    /// <exception cref="InvalidOperationException">
    ///     No service provider or matching registration is available.
    /// </exception>
    public TComponent GetComponent<TComponent>() where TComponent : notnull
    {
        var services = Services ?? throw new InvalidOperationException("Rendering this component requires a service provider.");
        return services.GetRequiredService<TComponent>();
    }

    /// <summary>Invokes an existing component using this render context.</summary>
    /// <typeparam name="TArguments">The component input type.</typeparam>
    /// <param name="writer">The shared output writer.</param>
    /// <param name="component">The component instance; this method does not dispose it.</param>
    /// <param name="arguments">The input passed to the component.</param>
    /// <returns>An operation covering the component output.</returns>
    /// <remarks>Generated templates write their compiled component comment before calling this method.</remarks>
    public ValueTask RenderComponentAsync<TArguments>(SavannaWriter writer, ISavannaComponent<TArguments> component, TArguments arguments)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(component);

        CancellationToken.ThrowIfCancellationRequested();

        return component.RenderAsync(writer, arguments, this);
    }

    /// <summary>Resolves a component and renders it using this context.</summary>
    /// <typeparam name="TComponent">The component service type.</typeparam>
    /// <typeparam name="TArguments">The component input type.</typeparam>
    /// <param name="writer">The shared output writer.</param>
    /// <param name="arguments">The input passed to the component.</param>
    /// <returns>An operation covering the component output.</returns>
    /// <exception cref="InvalidOperationException">
    ///     No service provider or component registration is available.
    /// </exception>
    public ValueTask RenderComponentAsync<TComponent, TArguments>(SavannaWriter writer, TArguments arguments) where TComponent : ISavannaComponent<TArguments>
    {
        CancellationToken.ThrowIfCancellationRequested();

        var services = Services ?? throw new InvalidOperationException("Rendering this component requires a service provider.");
        return RenderComponentAsync(writer, services.GetRequiredService<TComponent>(), arguments);
    }
}
