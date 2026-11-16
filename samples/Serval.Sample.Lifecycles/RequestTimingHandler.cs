#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Globalization;
using Serval.Lifecycle;

namespace Serval.Sample.Lifecycles;

public sealed class RequestTimingHandler : RequestLifecycleHandler
{
    private long _startedAt;

    private readonly RequestDetails                _details;
    private readonly ILogger<RequestTimingHandler> _logger;

    public RequestTimingHandler(RequestDetails details, ILogger<RequestTimingHandler> logger)
    {
        _details = details;
        _logger  = logger;
    }

    public override ValueTask OnRequestReceivedAsync(RequestContext context, CancellationToken ct)
    {
        _startedAt  = Stopwatch.GetTimestamp();
        _details.Id = Guid.NewGuid().ToString("N");
        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("{TraceId}: timing received request {RequestId}.", context.TraceId, _details.Id);
        }

        return ValueTask.CompletedTask;
    }

    public override ValueTask OnResponseStartingAsync(RequestContext context, CancellationToken ct)
    {
        var elapsed = Stopwatch.GetElapsedTime(_startedAt).TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture);
        context.Response.Headers["Server-Timing"] = $"app;dur={elapsed}";
        context.Response.Headers["X-Request-Id"]  = _details.Id;
        context.Response.Headers["X-Trace-Id"]    = context.TraceId;
        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("{TraceId}: timing starting ({StatusCode}).", context.TraceId, context.Response.StatusCode);
        }

        return ValueTask.CompletedTask;
    }

    public override ValueTask OnRequestCompletedAsync(RequestContext context, RequestCompletion completion, CancellationToken ct)
    {
        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("{TraceId}: timing completed request {RequestId} after {ElapsedMs:F1} ms.",
                context.TraceId, _details.Id, completion.Elapsed.TotalMilliseconds);
        }

        return ValueTask.CompletedTask;
    }
}
