#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.Extensions.Logging;
using System.Diagnostics;
using Serval.Middleware;

namespace Serval.Sample.Middleware;

public sealed class WorkTimingMiddleware : IMiddleware
{
    private readonly ILogger<WorkTimingMiddleware> _logger;

    public WorkTimingMiddleware(ILogger<WorkTimingMiddleware> logger)
    {
        _logger = logger;
    }

    public async ValueTask InvokeAsync(RequestContext context, RequestDelegate next)
    {
        var start = Stopwatch.GetTimestamp();
        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("{TraceId}: route middleware entered.", context.TraceId);
        }
        try
        {
            await next(context);
        }
        finally
        {
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("{TraceId}: route middleware exited after {ElapsedMs:F1} ms.",
                    context.TraceId, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            }
        }
    }
}
