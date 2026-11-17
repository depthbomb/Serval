#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Serval.Http;
using Serval.Http.Constants;
using Serval.Middleware;

namespace Serval.Routing;

/// <summary>Executes matched endpoints and handles method errors, allowing unmatched paths to continue.</summary>
public sealed class RoutingMiddleware : IMiddleware
{
    private readonly IEndpointMatcher _matcher;
    private readonly bool _automaticOptions;

    /// <summary>Creates routing middleware with optional automatic OPTIONS responses.</summary>
    public RoutingMiddleware(IEndpointMatcher matcher, bool automaticOptions = true)
    {
        _matcher          = matcher ?? throw new ArgumentNullException(nameof(matcher));
        _automaticOptions = automaticOptions;
    }

    /// <inheritdoc />
    public async ValueTask InvokeAsync(RequestContext context, RequestDelegate next)
    {
        if (!await TryHandleAsync(context))
        {
            await next(context);
        }
    }

    /// <summary>Handles a route or method response. Returns false only when the path is unmatched.</summary>
    public async ValueTask<bool> TryHandleAsync(RequestContext context)
    {
        var match = _matcher.Match(context);
        if (match.Endpoint is not null)
        {
            await match.ExecuteAsync(context);
            return true;
        }

        if (match.AllowedMethods.Count == 0)
        {
            return false;
        }

        var methods = _automaticOptions ? match.AllowedMethods.Append(HttpMethods.Options) : match.AllowedMethods;
        var allow = string.Join(", ", methods.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
        if (_automaticOptions && context.Request.Method == HttpMethods.Options)
        {
            context.Response.StatusCode = StatusCodes.NoContent;
            context.Response.Headers[HeaderNames.Allow] = allow;
            return true;
        }

        throw new HttpErrorException(StatusCodes.MethodNotAllowed, "This HTTP method is not supported for this route.")
        {
            Headers = { [HeaderNames.Allow] = allow },
        };
    }
}
