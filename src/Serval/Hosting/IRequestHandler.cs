#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Serval.Http;

namespace Serval.Hosting;

/// <summary>Runs an incoming request through the framework pipeline.</summary>
public interface IRequestHandler
{
    /// <summary>Dispatches a request and completes or aborts its response.</summary>
    /// <param name="request">The transport's incoming request.</param>
    /// <param name="response">The transport's response writer.</param>
    /// <param name="cancellationToken">Signals a disconnected client or transport shutdown.</param>
    /// <returns>An operation that completes after request processing and scope cleanup.</returns>
    /// <remarks>
    ///     The framework creates an asynchronous service scope and links its request deadline to the supplied cancellation
    ///     token.
    /// </remarks>
    /// <exception cref="RequestTransportException">
    ///     The transport must handle a protocol failure without writing an application error response.
    /// </exception>
    ValueTask HandleAsync(Request request, Response response, CancellationToken cancellationToken);
}

/// <summary>Handles application failures while the response can still be changed.</summary>
public interface IRequestExceptionHandler
{
    /// <summary>Writes an error response before headers are committed.</summary>
    /// <param name="context">The current request and its scoped services.</param>
    /// <param name="exception">The failure that ended normal request processing.</param>
    /// <returns>An operation that completes when the error response has been written.</returns>
    /// <remarks>
    ///     The framework completes the response afterward. If creating the request scope failed, context.Services cannot
    ///     resolve dependencies. Failures after headers start, and failures during scope disposal, are logged instead. The
    ///     request token may already be canceled when this method runs.
    /// </remarks>
    ValueTask HandleAsync(RequestContext context, Exception exception);
}
