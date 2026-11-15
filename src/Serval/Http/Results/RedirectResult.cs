#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Serval.Http.Constants;

namespace Serval.Http.Results;

/// <summary>Defers an HTTP redirect until result execution.</summary>
public sealed class RedirectResult : IActionResult
{
    /// <summary>The redirect target written to the Location header.</summary>
    public string Location { get; }

    /// <summary>The redirect status validated when the result executes.</summary>
    public int StatusCode { get; }

    /// <summary>Creates a redirect to the supplied location.</summary>
    /// <param name="location">The Location header value.</param>
    /// <param name="statusCode">One of 301, 302, 303, 307, or 308; defaults to 303.</param>
    public RedirectResult(string location, int statusCode = StatusCodes.SeeOther)
    {
        Location   = location;
        StatusCode = statusCode;
    }

    /// <inheritdoc />
    public ValueTask ExecuteAsync(RequestContext context) => context.Response.RedirectAsync(Location, StatusCode, context.RequestAborted);
}
