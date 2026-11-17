#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using System.Diagnostics;
using System.Security.Claims;
using Serval.Http;
using Serval.Routing;

namespace Serval;

/// <summary>Carries the request, response, and application state for one pipeline execution.</summary>
/// <remarks>
///     Keep per-request state here. The context and its mutable collections are not intended for concurrent mutation.
/// </remarks>
public sealed class RequestContext
{
    /// <summary>The incoming request and its body-reading helpers.</summary>
    public Request Request { get; }

    /// <summary>The response being built for this request.</summary>
    public Response Response { get; }

    /// <summary>The request's scoped service provider; its lifetime ends after request processing.</summary>
    public IServiceProvider Services { get; }

    /// <summary>The generator for paths to named routes.</summary>
    public IUrlGenerator Urls { get; }

    /// <summary>Signals the request deadline, client disconnect, or transport shutdown.</summary>
    public CancellationToken RequestAborted { get; }

    /// <summary>The identifier assigned when this request context was created.</summary>
    /// <remarks>For incoming requests, the application can customize it through <see cref="ServalOptions.TraceIdGenerator" />.</remarks>
    public string TraceId { get; }

    /// <summary>Decoded values captured by the selected route, keyed by parameter name.</summary>
    public IReadOnlyDictionary<string, string> RouteValues { get; internal set; }

    /// <summary>The selected endpoint, or null for files, redirects, unmatched routes, and automatic OPTIONS.</summary>
    public Endpoint? Endpoint { get; internal set; }

    /// <summary>A shared per-request bag for middleware and application state.</summary>
    public IDictionary<object, object?> Items { get; }

    /// <summary>The current principal. Starts anonymous and is updated by authentication.</summary>
    public ClaimsPrincipal User { get; set; }

    internal CancellationTokenSource? TimeoutSource { get; init; }
    internal long                     StartedAt     { get; init; } = Stopwatch.GetTimestamp();

    /// <summary>Creates the context for one request and its scoped services.</summary>
    /// <param name="request">The incoming request.</param>
    /// <param name="response">The response to write.</param>
    /// <param name="services">The service provider for this request's scope.</param>
    /// <param name="urls">The application's named-route URL generator.</param>
    /// <param name="requestAborted">The linked request deadline and transport cancellation token.</param>
    /// <remarks>
    ///     Direct construction uses the current activity's trace ID or a GUID without separators. The framework uses
    ///     <see cref="ServalOptions.TraceIdGenerator" /> when creating contexts for incoming requests.
    /// </remarks>
    public RequestContext(Request           request,
                          Response          response,
                          IServiceProvider  services,
                          IUrlGenerator     urls,
                          CancellationToken requestAborted)
        : this(request, response, services, urls, CreateDefaultTraceId(), requestAborted) {}

    internal RequestContext(Request           request,
                            Response          response,
                            IServiceProvider  services,
                            IUrlGenerator     urls,
                            string            traceId,
                            CancellationToken requestAborted)
    {
        Request        = request;
        Response       = response;
        Services       = services;
        Urls           = urls;
        RequestAborted = requestAborted;
        TraceId        = traceId;
        RouteValues    = new Dictionary<string, string>();
        Items          = new Dictionary<object, object?>();
        User           = new ClaimsPrincipal(new ClaimsIdentity());
    }

    /// <summary>Stops the framework's deadline timer for this request.</summary>
    /// <remarks>
    ///     Useful for long-lived streams. Client disconnect and transport cancellation still apply, and an already canceled
    ///     token cannot be reset.
    /// </remarks>
    public void DisableRequestTimeout() => TimeoutSource?.CancelAfter(Timeout.InfiniteTimeSpan);

    internal void ApplyEndpoint(Endpoint endpoint)
    {
        Endpoint = endpoint;
        Request.ApplyBodyPolicy(endpoint.MaxRequestBodyBytes, endpoint.Forms);
        if (endpoint.RequestTimeout is {} timeout)
        {
            var infinite  = timeout == Timeout.InfiniteTimeSpan;
            var remaining = infinite ? timeout : timeout - Stopwatch.GetElapsedTime(StartedAt);
            TimeoutSource?.CancelAfter(infinite ? Timeout.InfiniteTimeSpan : remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
            if (!infinite && remaining <= TimeSpan.Zero)
            {
                TimeoutSource?.Cancel();
                RequestAborted.ThrowIfCancellationRequested();
            }
        }
    }

    internal static string CreateDefaultTraceId() => Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N");
}
