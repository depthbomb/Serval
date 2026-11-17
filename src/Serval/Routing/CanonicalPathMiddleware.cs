#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Serval.Http;
using Serval.Http.Constants;
using Serval.Middleware;

namespace Serval.Routing;

/// <summary>Applies an explicit trailing-slash redirect policy.</summary>
public sealed class CanonicalPathMiddleware : IMiddleware
{
    private readonly TrailingSlashBehavior _behavior;

    /// <summary>Creates a canonical-path component. Ignore and Strict do not redirect.</summary>
    public CanonicalPathMiddleware(TrailingSlashBehavior behavior)
    {
        if (!Enum.IsDefined(behavior))
        {
            throw new ArgumentOutOfRangeException(nameof(behavior));
        }

        _behavior = behavior;
    }

    /// <inheritdoc />
    public async ValueTask InvokeAsync(RequestContext context, RequestDelegate next)
    {
        if (!await TryRedirectAsync(context))
        {
            await next(context);
        }
    }

    /// <summary>Redirects when required and returns whether the request was handled.</summary>
    public async ValueTask<bool> TryRedirectAsync(RequestContext context)
    {
        var path = context.Request.Url.AbsolutePath;
        if (path.StartsWith("//", StringComparison.Ordinal))
        {
            throw new HttpErrorException(StatusCodes.BadRequest, "The request path must start with a single slash.");
        }

        var trailing = path.Length > 1 && path.EndsWith('/');
        var redirect = path != "/" && (_behavior == TrailingSlashBehavior.RedirectToWithoutSlash && trailing || _behavior == TrailingSlashBehavior.RedirectToWithSlash && !trailing);
        if (!redirect)
        {
            return false;
        }

        var canonical = _behavior == TrailingSlashBehavior.RedirectToWithSlash ? path + "/" : path.TrimEnd('/');
        await context.Response.RedirectAsync(canonical + context.Request.Url.Query, StatusCodes.PermanentRedirect, context.RequestAborted);
        return true;
    }
}
