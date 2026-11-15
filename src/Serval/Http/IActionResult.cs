#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

namespace Serval.Http;

/// <summary>Represents response work deferred until a controller action returns.</summary>
public interface IActionResult
{
    /// <summary>Applies this result to the current response.</summary>
    /// <param name="context">The current request and its scoped services.</param>
    /// <returns>An operation that completes when the result has been applied.</returns>
    /// <remarks>
    ///     Use context.RequestAborted for asynchronous work. The framework completes the response after this method
    ///     returns.
    /// </remarks>
    ValueTask ExecuteAsync(RequestContext context);
}
