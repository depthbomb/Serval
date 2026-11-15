#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Serval.Http;
using Serval.Http.Constants;

namespace Serval.Savanna;

/// <summary>Renders a view as a UTF-8 HTML action result.</summary>
/// <typeparam name="TModel">The view's model type.</typeparam>
/// <remarks>
///     With a buffer limit, rendering finishes before any response bytes are written. Streaming can commit headers before
///     a
///     later rendering error occurs.
/// </remarks>
public sealed class SavannaResult<TModel> : IActionResult
{
    private readonly int?             _bufferLimit;
    private readonly TModel           _model;
    private readonly int?             _statusCode;
    private readonly SavannaView<TModel> _view;

    /// <summary>Creates a deferred HTML result for a view and model.</summary>
    /// <param name="view">The compiled or delegate-based view.</param>
    /// <param name="model">The model passed to the view.</param>
    /// <param name="statusCode">An optional HTTP status override; null preserves the current status.</param>
    /// <param name="bufferLimit">A positive maximum rendered byte count for buffering, or null to stream directly.</param>
    /// <exception cref="ArgumentNullException">The view is null.</exception>
    public SavannaResult(SavannaView<TModel> view, TModel model, int? statusCode = null, int? bufferLimit = null)
    {
        ArgumentNullException.ThrowIfNull(view);

        _view        = view;
        _model       = model;
        _statusCode  = statusCode;
        _bufferLimit = bufferLimit;
    }

    /// <inheritdoc />
    /// <remarks>Sets the HTML content type and uses request services and cancellation for rendering.</remarks>
    /// <exception cref="InvalidOperationException">
    ///     The response has started or the buffered output exceeds its limit.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">The supplied buffer limit is not positive.</exception>
    public async ValueTask ExecuteAsync(RequestContext context)
    {
        if (context.Response.HasStarted)
        {
            throw new InvalidOperationException("A view result must execute before the response starts.");
        }

        if (_statusCode is { } status)
        {
            context.Response.StatusCode = status;
        }

        context.Response.Headers[HeaderNames.ContentType] = MimeTypes.Html;
        var renderContext = new SavannaRenderContext(context.Services, context.RequestAborted);
        if (_bufferLimit is { } limit)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

            using var body = new MemoryStream();
            using var bufferedWriter = new SavannaWriter((bytes, token) =>
            {
                if (bytes.Length > limit - body.Length)
                {
                    throw new InvalidOperationException("The rendered view exceeds the configured size limit.");
                }

                return body.WriteAsync(bytes, token);
            });
            await _view.RenderAsync(bufferedWriter, _model, renderContext);
            await bufferedWriter.FlushAsync(context.RequestAborted);
            await context.Response.WriteAsync(body.GetBuffer().AsMemory(0, checked((int)body.Length)), context.RequestAborted);
            return;
        }

        using var writer = new SavannaWriter(context.Response.WriteAsync);
        await _view.RenderAsync(writer, _model, renderContext);
        await writer.FlushAsync(context.RequestAborted);
    }
}

/// <summary>Creates deferred Savanna view results from a request context.</summary>
public static class SavannaContextExtensions
{
    extension(RequestContext context)
    {
        /// <summary>Creates a result that renders a Savanna view when executed.</summary>
        /// <typeparam name="TModel">The view's model type.</typeparam>
        /// <param name="view">The compiled or delegate-based view.</param>
        /// <param name="model">The model passed to the view.</param>
        /// <param name="statusCode">An optional HTTP status override; null preserves the current status.</param>
        /// <param name="bufferLimit">
        ///     A positive maximum rendered byte count for buffering, or null to stream directly.
        /// </param>
        /// <returns>The deferred HTML view result.</returns>
        public SavannaResult<TModel> View<TModel>(SavannaView<TModel> view, TModel model, int? statusCode = null, int? bufferLimit = null)
            => new(view, model, statusCode, bufferLimit);

        /// <summary>Wraps a deferred Savanna result in a completed value task.</summary>
        /// <typeparam name="TModel">The view's model type.</typeparam>
        /// <param name="view">The compiled or delegate-based view.</param>
        /// <param name="model">The model passed to the view.</param>
        /// <param name="statusCode">An optional HTTP status override; null preserves the current status.</param>
        /// <param name="bufferLimit">
        ///     A positive maximum rendered byte count for buffering, or null to stream directly.
        /// </param>
        /// <returns>A completed task containing the result; rendering has not started.</returns>
        public ValueTask<IActionResult> ViewAsync<TModel>(SavannaView<TModel> view, TModel model, int? statusCode = null, int? bufferLimit = null)
            => ValueTask.FromResult<IActionResult>(context.View(view, model, statusCode, bufferLimit));
    }
}
