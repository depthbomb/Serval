#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

namespace Serval.Http.Results;

/// <summary>Changes the response status without writing a body.</summary>
public sealed class StatusCodeResult : IActionResult
{
    /// <summary>The status applied when the result executes.</summary>
    public int StatusCode { get; }

    /// <summary>Creates a result that sets the response status code.</summary>
    /// <param name="statusCode">The final HTTP status, validated during execution.</param>
    public StatusCodeResult(int statusCode)
    {
        StatusCode = statusCode;
    }

    /// <inheritdoc />
    public ValueTask ExecuteAsync(RequestContext context)
    {
        context.Response.StatusCode = StatusCode;
        return ValueTask.CompletedTask;
    }
}
