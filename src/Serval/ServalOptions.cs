#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using System.Text.Json;
using Serval.Http;
using Serval.Http.StaticFiles;

namespace Serval;

/// <summary>Controls how routing treats a final slash in a URL path.</summary>
public enum TrailingSlashBehavior
{
    /// <summary>Matches paths with or without a final slash.</summary>
    Ignore,

    /// <summary>Requires the path's final slash to match the route template.</summary>
    Strict,

    /// <summary>Redirects non-root paths ending in a slash to the version without one, using status 308.</summary>
    RedirectToWithoutSlash,

    /// <summary>Redirects non-root paths without a final slash to the version with one, using status 308.</summary>
    RedirectToWithSlash,
}

/// <summary>Configures routing, request limits, trace IDs, serialization, and static files before the host starts.</summary>
public sealed class ServalOptions
{
    /// <summary>How a final path slash is handled. Defaults to ignoring it.</summary>
    public TrailingSlashBehavior TrailingSlashes { get; set; } = TrailingSlashBehavior.Ignore;

    /// <summary>Whether literal route segments are case-sensitive. Defaults to false.</summary>
    public bool CaseSensitivePaths { get; set; }

    /// <summary>
    ///     Whether HEAD can fall back to a GET endpoint when no matching HEAD endpoint is selected. Defaults to true.
    /// </summary>
    public bool AutomaticHead { get; set; } = true;

    /// <summary>Whether matching routes get an automatic OPTIONS response and Allow header. Defaults to true.</summary>
    public bool AutomaticOptions { get; set; } = true;

    /// <summary>The positive request-body limit in bytes, including cumulative body reads. Defaults to 1 MiB.</summary>
    public long MaxRequestBodyBytes { get; set; } = 1024 * 1024;

    /// <summary>The positive header-size limit used by the framework and transports. Defaults to 32 KiB.</summary>
    public int MaxRequestHeaderBytes { get; set; } = 32 * 1024;

    /// <summary>The positive concurrency limit enforced by transports. Defaults to 1,024.</summary>
    public int MaxConcurrentRequests { get; set; } = 1024;

    /// <summary>
    ///     The request deadline, defaulting to 30 seconds. Use <see cref="Timeout.InfiniteTimeSpan" /> to disable it.
    /// </summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>The separate cooperative budget for completion callbacks and hooks, defaulting to five seconds.</summary>
    /// <remarks>
    ///     All completion hooks are still invoked when this budget expires. They must observe their supplied token.
    ///     Callbacks are awaited before disposing request resources; they are never abandoned in the background.
    ///     Use Timeout.InfiniteTimeSpan to disable the budget. Resource disposal itself is always awaited.
    /// </remarks>
    public TimeSpan CompletionTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    ///     Creates the trace ID for each request. Defaults to the current activity's trace ID, or a GUID without separators
    ///     when no activity exists.
    /// </summary>
    /// <remarks>
    ///     Configure this before the host starts. The callback belongs to this Serval instance and runs once per request,
    ///     before creating its service scope. A custom callback takes precedence over the current activity but does not
    ///     change it. The callback must be thread-safe and return a non-null, non-whitespace identifier suitable for
    ///     correlating requests. Exceptions and invalid results go through the request exception handler with a default
    ///     trace ID; the callback is not retried. The returned ID is available through <see cref="RequestContext.TraceId" />.
    /// </remarks>
    public Func<string> TraceIdGenerator { get; set; } = RequestContext.CreateDefaultTraceId;

    /// <summary>
    ///     JSON serialization settings, initialized with web defaults and made read-only when routes are validated.
    /// </summary>
    public JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web);

    /// <summary>Limits and temporary storage settings for form fields, uploaded files, and parsed POST data.</summary>
    public FormOptions Forms { get; } = new();

    /// <summary>Settings for serving files alongside controller routes.</summary>
    public StaticFileOptions StaticFiles { get; } = new();

    /// <summary>
    ///     Custom route constraints keyed by case-insensitive names. Each predicate receives the decoded value and the
    ///     template's constraint arguments.
    /// </summary>
    public Dictionary<string, Func<string, string[], bool>> RouteConstraints { get; } = new(StringComparer.OrdinalIgnoreCase);

    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(TraceIdGenerator);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxRequestBodyBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxRequestHeaderBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxConcurrentRequests);

        if (RequestTimeout != Timeout.InfiniteTimeSpan && (RequestTimeout <= TimeSpan.Zero || RequestTimeout.TotalMilliseconds > uint.MaxValue - 1))
        {
            throw new ArgumentOutOfRangeException(nameof(RequestTimeout));
        }

        if (!Enum.IsDefined(TrailingSlashes))
        {
            throw new ArgumentOutOfRangeException(nameof(TrailingSlashes));
        }

        if (CompletionTimeout != Timeout.InfiniteTimeSpan && (CompletionTimeout <= TimeSpan.Zero || CompletionTimeout.TotalMilliseconds > uint.MaxValue - 1))
        {
            throw new ArgumentOutOfRangeException(nameof(CompletionTimeout));
        }

        Json.MakeReadOnly(true);
        Forms.Validate();
        StaticFiles.Validate();
    }
}
