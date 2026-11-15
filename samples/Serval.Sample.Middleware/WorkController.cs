#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Serval.Http;
using Serval.Middleware;
using Serval.Routing;

namespace Serval.Sample.Middleware;

[Controller]
public sealed class WorkController : ControllerBase
{
    private readonly RequestStamp _stamp;

    public WorkController(RequestStamp stamp)
    {
        _stamp = stamp;
    }

    [Get("/")]
    public ValueTask<IActionResult> IndexAsync() => TextAsync("""
        Middleware: wrapping and short-circuiting requests

        GET /work returns the same scoped request ID logged by application middleware.
        Repeat the request to see a new scope. The trace ID correlates the console logs.
        Application middleware wraps route middleware. Route priorities run in ascending order:
        maintenance (0), then work timing (10). Equal priorities keep registration order.

        Watch the console for application entry, route entry, route exit, and application exit.
        Awaiting next wraps downstream work; finally runs even when an action fails.

        GET /crash demonstrates unwinding after an action fails. Middleware exits before the
        framework writes the 500 response. GET /missing runs only application middleware.

        Restart with --maintenance true to return 503 from /work before the controller runs.
        Application middleware still runs; work timing and the controller are skipped.

        Use the Lifecycles sample for request initialization, final response headers, and final
        outcome logging. Those hooks run at fixed phases and have no next delegate.
        """);

    [Get("/work", Name = "work")]
    [UseMiddleware(typeof(MaintenanceMiddleware))]
    [UseMiddleware(typeof(WorkTimingMiddleware), Priority = 10)]
    public ValueTask<IActionResult> WorkAsync(RequestContext context)
        => JsonAsync(new
        {
            requestId = _stamp.Id,
            traceId   = context.TraceId,
            message   = "The controller ran.",
        });

    [Get("/crash")]
    [UseMiddleware(typeof(WorkTimingMiddleware), Priority = 10)]
    public ValueTask<IActionResult> CrashAsync()
        => throw new InvalidOperationException("This sample failure demonstrates middleware unwinding.");
}
