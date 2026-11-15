#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Claims;
using System.Text.Json.Serialization.Metadata;
using Serval.Binding;
using Serval.Http;
using Serval.Http.Constants;
using Serval.Http.Results;
using Serval.Routing;
using Serval.Validation;

namespace Serval;

/// <summary>Provides request properties and result helpers for controller actions.</summary>
/// <remarks>
///     Inheritance is optional. Serval attaches the context before invoking an action and clears it after the action
///     result finishes, including when either operation throws. Context-dependent members are unavailable in the
///     constructor, during disposal, or after invocation. Controllers are scoped by default; sharing one instance
///     across concurrent requests is not supported. Both generated and manually registered actions use this lifecycle.
/// </remarks>
public abstract class ControllerBase
{
    /// <summary>The current action's request context.</summary>
    /// <exception cref="InvalidOperationException">The controller is not handling an action or its result.</exception>
    public RequestContext Context => Volatile.Read(ref _context)
                                     ?? throw new InvalidOperationException("The controller context is only available during an action and its result execution.");

    /// <summary>The current HTTP request and its body-reading helpers.</summary>
    public Request Request => Context.Request;

    /// <summary>The current response, including headers, cookies, and streaming operations.</summary>
    public Response Response => Context.Response;

    /// <summary>The dependency injection provider for the current request scope.</summary>
    public IServiceProvider Services => Context.Services;

    /// <summary>The current principal, shared with authentication and authorization middleware.</summary>
    public ClaimsPrincipal User
    {
        get => Context.User;
        set => Context.User = value;
    }

    /// <summary>The named-route URL generator, including generated typed URL extensions.</summary>
    public IUrlGenerator Urls => Context.Urls;

    /// <summary>Signals a request deadline, client disconnect, or transport shutdown.</summary>
    public CancellationToken RequestAborted => Context.RequestAborted;

    /// <summary>The decoded parameters captured by the selected route.</summary>
    public IReadOnlyDictionary<string, string> RouteValues => Context.RouteValues;

    /// <summary>The per-request state shared with middleware and other request services.</summary>
    public IDictionary<object, object?> Items => Context.Items;

    /// <summary>The current request's tracing identifier.</summary>
    public string TraceId => Context.TraceId;

    private RequestContext? _context;

    internal void AttachContext(RequestContext context)
    {
        // A mistakenly shared controller must never swap another request's identity or services.
        if (Interlocked.CompareExchange(ref _context, context, null) is not null)
        {
            throw new InvalidOperationException("A controller instance cannot handle concurrent actions. Register controllers as scoped services.");
        }
    }

    internal void DetachContext() => Interlocked.Exchange(ref _context, null);

    /// <summary>Runs registered input validators, including for models assembled from forms or POST values.</summary>
    protected ValueTask ValidateAsync<T>(T input) => InputValidation.ValidateAsync(Context, input);

    /// <summary>Creates a deferred UTF-8 text result.</summary>
    /// <param name="text">The response text.</param>
    /// <param name="statusCode">An optional final status; null preserves the response's current status.</param>
    /// <returns>The result to return from the action.</returns>
    protected TextResult Text(string text, int? statusCode = null) => new(text, statusCode);

    /// <summary>Creates a deferred JSON result.</summary>
    /// <typeparam name="T">The serialized value type.</typeparam>
    /// <param name="value">The value to serialize.</param>
    /// <param name="typeInfo">Explicit metadata, or null to use the application's configured JSON options.</param>
    /// <param name="statusCode">An optional final status; null preserves the response's current status.</param>
    /// <returns>The result to return from the action.</returns>
    protected JsonResult<T> Json<T>(T value, JsonTypeInfo<T>? typeInfo = null, int? statusCode = null)
        => new(value, typeInfo, statusCode);

    /// <summary>Creates a result that opens and streams a file when executed.</summary>
    /// <param name="path">The file path.</param>
    /// <param name="contentType">The media type, or null for application/octet-stream.</param>
    /// <param name="downloadName">An optional attachment filename.</param>
    /// <returns>The deferred file result.</returns>
    protected FileResult File(string path, string? contentType = null, string? downloadName = null)
        => new(path, contentType, downloadName);

    /// <summary>Creates a result that leaves the response unchanged.</summary>
    /// <returns>The shared empty result.</returns>
    protected EmptyResult Empty() => EmptyResult.Instance;

    /// <summary>Creates a result that sets only the final HTTP status.</summary>
    /// <param name="statusCode">The final status, from 200 through 599.</param>
    /// <returns>The deferred status result.</returns>
    protected StatusCodeResult StatusCode(int statusCode) => new(statusCode);

    /// <summary>Creates an HTTP redirect result.</summary>
    /// <param name="location">The Location header value.</param>
    /// <param name="statusCode">A redirect status: 301, 302, 303, 307, or 308; defaults to 303.</param>
    /// <returns>The deferred redirect result.</returns>
    protected RedirectResult Redirect(string location, int statusCode = StatusCodes.SeeOther)
        => new(location, statusCode);

    /// <summary>Wraps a deferred text result in a completed value task; no bytes are written yet.</summary>
    /// <param name="text">The response text.</param>
    /// <param name="statusCode">An optional final status.</param>
    /// <returns>A completed task containing the result.</returns>
    protected ValueTask<IActionResult> TextAsync(string text, int? statusCode = null) => ValueTask.FromResult<IActionResult>(Text(text, statusCode));

    /// <summary>Wraps a deferred JSON result in a completed value task; serialization happens during execution.</summary>
    /// <typeparam name="T">The serialized value type.</typeparam>
    /// <param name="value">The value to serialize.</param>
    /// <param name="typeInfo">Explicit metadata, or null to use the application's configured JSON options.</param>
    /// <param name="statusCode">An optional final status.</param>
    /// <returns>A completed task containing the result.</returns>
    protected ValueTask<IActionResult> JsonAsync<T>(T value, JsonTypeInfo<T>? typeInfo = null, int? statusCode = null)
        => ValueTask.FromResult<IActionResult>(Json(value, typeInfo, statusCode));

    /// <summary>Wraps a deferred file result in a completed value task; the file has not been opened.</summary>
    /// <param name="path">The file path.</param>
    /// <param name="contentType">The media type, or null for application/octet-stream.</param>
    /// <param name="downloadName">An optional attachment filename.</param>
    /// <returns>A completed task containing the result.</returns>
    protected ValueTask<IActionResult> FileAsync(string path, string? contentType = null, string? downloadName = null)
        => ValueTask.FromResult<IActionResult>(File(path, contentType, downloadName));

    /// <summary>Parses a route parameter using invariant culture.</summary>
    /// <typeparam name="T">The target type that can parse a string.</typeparam>
    /// <param name="name">The route parameter name.</param>
    /// <returns>The parsed value.</returns>
    /// <exception cref="HttpErrorException">The parameter is missing or invalid; the status is 400.</exception>
    protected T RouteValue<T>(string name) where T : IParsable<T>
        => RequestBinding.Parse<T>(RouteValues.GetValueOrDefault(name), "Route", name);

    /// <summary>Gets a required route string.</summary>
    protected string RouteValue(string name) => RouteValue<string>(name);

    /// <summary>Gets a route scalar, using the fallback only when absent.</summary>
    protected T RouteValue<T>(string name, T fallback) where T : IParsable<T> => RouteValues.ContainsKey(name) ? RouteValue<T>(name) : fallback;

    /// <summary>Tries to parse a route scalar.</summary>
    protected bool TryRouteValue<T>(string name, [MaybeNullWhen(false)] out T value) where T : IParsable<T>
    {
        value = default;
        return RouteValues.TryGetValue(name, out var text) && T.TryParse(text, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>Gets a required route enum.</summary>
    protected T RouteEnum<T>(string name) where T : struct, Enum => RequestBinding.ParseEnum<T>(RouteValues.GetValueOrDefault(name), "Route", name);

    /// <summary>Gets a required query string.</summary>
    protected string QueryValue(string name) => Request.QueryValue(name);

    /// <summary>Gets exactly one required query scalar.</summary>
    protected T QueryValue<T>(string name) where T : IParsable<T> => Request.QueryValue<T>(name);

    /// <summary>Gets a query scalar, using the fallback only when absent.</summary>
    protected T QueryValue<T>(string name, T fallback) where T : IParsable<T> => Request.QueryValue(name, fallback);

    /// <summary>Tries to parse exactly one query scalar.</summary>
    protected bool TryQueryValue<T>(string name, [MaybeNullWhen(false)] out T value) where T : IParsable<T> => Request.TryQueryValue(name, out value);

    /// <summary>Gets repeated query values in order.</summary>
    protected T[] QueryValues<T>(string name) where T : IParsable<T> => Request.QueryValues<T>(name);

    /// <summary>Gets a required query enum.</summary>
    protected T QueryEnum<T>(string name) where T : struct, Enum => Request.QueryEnum<T>(name);

    /// <summary>Reads and caches submitted form fields and uploaded files.</summary>
    protected ValueTask<RequestData> ReadFormAsync(CancellationToken ct = default) => Request.ReadFormAsync(ct);

    /// <summary>Reads and caches form data or top-level JSON properties.</summary>
    protected ValueTask<RequestData> ReadPostAsync(CancellationToken ct = default) => Request.ReadPostAsync(ct);

    /// <summary>Reads one required scalar from a form or JSON body.</summary>
    protected ValueTask<T> PostValueAsync<T>(string name, CancellationToken ct = default) where T : IParsable<T> => Request.PostValueAsync<T>(name, ct);

    /// <summary>Reads a submitted scalar, using the fallback only when absent.</summary>
    protected ValueTask<T> PostValueAsync<T>(string name, T fallback, CancellationToken ct = default) where T : IParsable<T> => Request.PostValueAsync(name, fallback, ct);

    /// <summary>Reads a top-level JSON property using explicit metadata.</summary>
    protected ValueTask<T> PostValueAsync<T>(string name, JsonTypeInfo<T> typeInfo, CancellationToken ct = default) => Request.PostValueAsync(name, typeInfo, ct);

    /// <summary>Reads repeated submitted fields or a JSON array as scalars.</summary>
    protected ValueTask<T[]> PostValuesAsync<T>(string name, CancellationToken ct = default) where T : IParsable<T> => Request.PostValuesAsync<T>(name, ct);

    /// <summary>Stops normal execution by raising an HTTP error.</summary>
    /// <param name="statusCode">An error status from 400 through 599.</param>
    /// <param name="message">An optional message exposed by the default error handler.</param>
    /// <exception cref="HttpErrorException">Always raised with the supplied status and message.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The status is outside 400 through 599.</exception>
    [DoesNotReturn]
    protected void Abort(int statusCode, string? message = null) => throw new HttpErrorException(statusCode, message);

    /// <summary>Raises an HTTP error when the condition is true.</summary>
    /// <param name="condition">The condition to evaluate.</param>
    /// <param name="statusCode">An error status from 400 through 599.</param>
    /// <param name="message">An optional message exposed by the default error handler.</param>
    /// <exception cref="HttpErrorException">The condition is true.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An error is raised with an invalid status.</exception>
    protected void AbortIf([DoesNotReturnIf(true)] bool condition, int statusCode, string? message = null)
    {
        if (condition)
        {
            Abort(statusCode, message);
        }
    }

    /// <summary>Raises an HTTP error when the condition is false.</summary>
    /// <param name="condition">The condition to evaluate.</param>
    /// <param name="statusCode">An error status from 400 through 599.</param>
    /// <param name="message">An optional message exposed by the default error handler.</param>
    /// <exception cref="HttpErrorException">The condition is false.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An error is raised with an invalid status.</exception>
    protected void AbortUnless([DoesNotReturnIf(false)] bool condition, int statusCode, string? message = null)
    {
        if (!condition)
        {
            Abort(statusCode, message);
        }
    }

    /// <summary>Stops the request deadline timer, while preserving cancellation on disconnect or transport shutdown.</summary>
    protected void DisableRequestTimeout() => Context.DisableRequestTimeout();

    /// <summary>Sends HTTP 103 hint fields using the current request's cancellation token.</summary>
    /// <param name="headers">The hint fields, usually Link headers describing resources to preload.</param>
    /// <returns>True when sent, or false when the transport does not support Early Hints.</returns>
    /// <remarks>Uses Response.SendEarlyHintsAsync; the final response remains writable after a successful send.</remarks>
    /// <exception cref="ArgumentNullException">The headers are null.</exception>
    /// <exception cref="ArgumentException">The hints contain framing or connection-specific fields.</exception>
    /// <exception cref="InvalidOperationException">No action is active, or the response has already started.</exception>
    /// <exception cref="OperationCanceledException">The request was canceled.</exception>
    protected ValueTask<bool> SendEarlyHintsAsync(Headers headers) => Response.SendEarlyHintsAsync(headers, RequestAborted);
}
