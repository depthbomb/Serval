#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using System.Text;

namespace Serval.Savanna;

/// <summary>Renders a model through a shared Savanna writer.</summary>
/// <typeparam name="TModel">The view's model type.</typeparam>
/// <param name="writer">The output writer.</param>
/// <param name="model">The view model.</param>
/// <param name="context">The active services and render settings.</param>
/// <returns>An operation that completes when the view body is rendered.</returns>
public delegate ValueTask SavannaRenderDelegate<in TModel>(SavannaWriter writer, TModel model, SavannaRenderContext context);

/// <summary>Provides typed entry points for rendering a complete view or a partial.</summary>
/// <typeparam name="TModel">The view's model type.</typeparam>
public sealed class SavannaView<TModel>
{
    /// <summary>The logical template name.</summary>
    public string Name { get; }

    private readonly SavannaRenderDelegate<TModel> _render;
    private readonly SavannaRenderDelegate<TModel> _renderPartial;

    /// <summary>Creates a reusable view from its rendering delegates.</summary>
    /// <param name="name">The nonempty logical template name.</param>
    /// <param name="render">The delegate that renders the view body.</param>
    /// <param name="renderPartial">The partial renderer, or null to use the complete-view renderer.</param>
    /// <remarks>Generated delegates include their compiled comments. Handwritten delegates own their complete output.</remarks>
    /// <exception cref="ArgumentException">The name is empty.</exception>
    /// <exception cref="ArgumentNullException">The name or render delegate is null.</exception>
    public SavannaView(string name, SavannaRenderDelegate<TModel> render, SavannaRenderDelegate<TModel>? renderPartial = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        Name = name;

        _render        = render        ?? throw new ArgumentNullException(nameof(render));
        _renderPartial = renderPartial ?? render;
    }

    /// <summary>Runs the complete-view renderer.</summary>
    /// <param name="writer">The writer, owned and flushed by the caller.</param>
    /// <param name="model">The view model.</param>
    /// <param name="context">The active services and render settings.</param>
    /// <returns>An operation that completes when rendering finishes.</returns>
    /// <remarks>This method does not flush or dispose the writer.</remarks>
    public ValueTask RenderAsync(SavannaWriter writer, TModel model, SavannaRenderContext context) => RenderCoreAsync(writer, model, context, false);

    /// <summary>Runs the partial renderer.</summary>
    /// <param name="writer">The writer, owned and flushed by the caller.</param>
    /// <param name="model">The view model.</param>
    /// <param name="context">The active services and render settings.</param>
    /// <returns>An operation that completes when rendering finishes.</returns>
    /// <remarks>This method does not flush or dispose the writer.</remarks>
    public ValueTask RenderPartialAsync(SavannaWriter writer, TModel model, SavannaRenderContext context) => RenderCoreAsync(writer, model, context, true);

    /// <summary>Renders the complete view into a bounded UTF-8 buffer and decodes it.</summary>
    /// <param name="model">The view model.</param>
    /// <param name="context">
    ///     The render context, or null to create one with current-culture formatting and no services.
    /// </param>
    /// <param name="maxBytes">The positive maximum UTF-8 byte count, including comments; defaults to 4 MiB.</param>
    /// <returns>The complete rendered HTML string.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The byte limit is not positive.</exception>
    /// <exception cref="InvalidOperationException">Rendered output exceeds the limit.</exception>
    public async ValueTask<string> RenderToStringAsync(TModel model, SavannaRenderContext? context = null, int maxBytes = 4 * 1024 * 1024)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);

        context ??= new SavannaRenderContext();

        using var stream = new MemoryStream();
        using var writer = new SavannaWriter((bytes, token) =>
            bytes.Length > maxBytes - stream.Length ? throw new InvalidOperationException("The rendered view exceeds the configured size limit.") : stream.WriteAsync(bytes, token));

        await RenderAsync(writer, model, context);
        await writer.FlushAsync(context.CancellationToken);
        return Encoding.UTF8.GetString(stream.GetBuffer(), 0, checked((int)stream.Length));
    }

    private ValueTask RenderCoreAsync(SavannaWriter writer, TModel model, SavannaRenderContext context, bool partial)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(context);

        context.CancellationToken.ThrowIfCancellationRequested();

        return (partial ? _renderPartial : _render)(writer, model, context);
    }
}
