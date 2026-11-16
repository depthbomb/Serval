#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.Extensions.Logging;
using Serval.Lifecycle;

namespace Serval.Sample.Lifecycles;

public sealed class ResponseHeadersHandler : RequestLifecycleHandler
{
    private readonly ILogger<ResponseHeadersHandler> _logger;

    public ResponseHeadersHandler(ILogger<ResponseHeadersHandler> logger)
    {
        _logger = logger;
    }

    public override ValueTask OnResponseStartingAsync(RequestContext context, CancellationToken ct)
    {
        // This also covers error responses after the exception handler has replaced the headers.
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("{TraceId}: headers starting ({StatusCode}).", context.TraceId, context.Response.StatusCode);
        }

        return ValueTask.CompletedTask;
    }
}
