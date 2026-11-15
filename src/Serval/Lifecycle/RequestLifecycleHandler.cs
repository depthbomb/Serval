#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

namespace Serval.Lifecycle;

/// <summary>Handles fixed phases of a request using one scoped instance through completion.</summary>
/// <remarks>
///     Register with ServalBuilder.AddRequestLifecycleHandler. Hooks run sequentially in ascending priority order;
///     ties keep registration order. Use middleware to wrap downstream execution or short-circuit requests.
/// </remarks>
public abstract class RequestLifecycleHandler
{
    /// <summary>Initializes request state before application middleware, routing, and static files.</summary>
    /// <remarks>
    ///     The parsed request and service scope exist, but no endpoint has been selected. Forwarded headers and
    ///     authentication middleware have not run. Do not start, write, complete, or abort the response here.
    /// </remarks>
    public virtual ValueTask OnRequestReceivedAsync(RequestContext context, CancellationToken ct) => ValueTask.CompletedTask;

    /// <summary>Adjusts final headers or status after local OnStarting callbacks, before headers are committed.</summary>
    /// <remarks>
    ///     Runs at most once, including error responses and upgrades. Do not write or start the response.
    ///     Uncalled hooks can run when an earlier startup failure is handled with an error response.
    /// </remarks>
    public virtual ValueTask OnResponseStartingAsync(RequestContext context, CancellationToken ct) => ValueTask.CompletedTask;

    /// <summary>Observes the final outcome after local OnCompleted callbacks and before request resources are disposed.</summary>
    /// <remarks>
    ///     Runs for resolved handlers even if an earlier hook failed. The response is no longer writable. Failures are
    ///     logged independently. The token has a separate cooperative cleanup budget; it is not RequestAborted.
    ///     Queue long-running work to a background service without retaining the context or scoped services.
    /// </remarks>
    public virtual ValueTask OnRequestCompletedAsync(RequestContext context, RequestCompletion completion, CancellationToken ct) => ValueTask.CompletedTask;
}
