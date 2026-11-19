#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Serval.Http;
using Serval.Http.Constants;
using Serval.Middleware;

namespace Serval.SignedUrls;

/// <summary>Requires a valid, unexpired signed URL before running a controller or action.</summary>
public sealed class RequireSignedUrlAttribute : MiddlewareAttribute
{
    /// <inheritdoc />
    /// <remarks>Missing, invalid, or expired URLs produce status 403 through the framework's exception handler.</remarks>
    public override ValueTask InvokeAsync(RequestContext context, RequestDelegate next)
        => !context.HasValidSignedUrl() ? throw new HttpErrorException(StatusCodes.Forbidden, "A valid signed URL is required.") : next(context);
}
