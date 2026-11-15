#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Serval.Http.Constants;
using Serval.Middleware;

namespace Serval.Sample.Middleware;

public sealed record MaintenanceOptions(bool Enabled);

public sealed class MaintenanceMiddleware : IMiddleware
{
    private readonly MaintenanceOptions _options;

    public MaintenanceMiddleware(MaintenanceOptions options)
    {
        _options = options;
    }

    public async ValueTask InvokeAsync(RequestContext context, RequestDelegate next)
    {
        if (_options.Enabled)
        {
            context.Response.Headers["Retry-After"] = "60";
            context.Response.StatusCode             = StatusCodes.ServiceUnavailable;
            await context.Response.WriteTextAsync("Maintenance is enabled. Try again later.", context.RequestAborted);
            return;
        }

        await next(context);
    }
}
