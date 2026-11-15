#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.Extensions.Logging;
using System.Diagnostics;
using Serval.Middleware;

namespace Serval.Sample.Middleware;

public sealed class PipelineTimingMiddleware : IMiddleware
{
    private readonly ILogger<PipelineTimingMiddleware> _logger;
    private readonly RequestStamp                      _stamp;

    public PipelineTimingMiddleware(ILogger<PipelineTimingMiddleware> logger, RequestStamp stamp)
    {
        _logger = logger;
        _stamp  = stamp;
    }

    public async ValueTask InvokeAsync(RequestContext context, RequestDelegate next)
    {
        var start = Stopwatch.GetTimestamp();
        _logger.LogInformation("{TraceId}: application middleware entered. Request {RequestId}.", context.TraceId, _stamp.Id);
        try
        {
            await next(context);
        }
        finally
        {
            // This measures downstream execution. The framework may still need to write an error response.
            _logger.LogInformation("{TraceId}: application middleware exited after {ElapsedMs:F1} ms.",
                context.TraceId, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }
    }
}
