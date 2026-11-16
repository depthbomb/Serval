#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.Extensions.Logging;
using Serval.Http;
using Serval.Routing;

namespace Serval.Sample.Lifecycles;

[Controller]
public sealed class LifecycleController : ControllerBase
{
    private readonly RequestDetails                _details;
    private readonly ILogger<LifecycleController> _logger;

    public LifecycleController(RequestDetails details, ILogger<LifecycleController> logger)
    {
        _details = details;
        _logger  = logger;
    }

    [Get("/")]
    public ValueTask<IActionResult> IndexAsync() => TextAsync("""
        Request lifecycle handlers

        Program.cs registers timing (-100), response headers (0), and audit (100).
        Lower priorities run first in every phase; equal priorities keep registration order.
        Handlers are scoped to one request and remain alive through completion.

        GET /work returns the ID initialized by OnRequestReceivedAsync and shared through DI.
        Inspect X-Request-Id, X-Trace-Id, Server-Timing, and X-Content-Type-Options.
        OnResponseStartingAsync sets these final headers, including on error responses.
        OnRequestCompletedAsync logs the final status, reason, duration, and failure.

        Try /crash (500), /missing (404), /lifecycle.json (static file), /work/ (redirect),
        /slow (request timeout), and /partial (response aborted after headers were sent).
        GET /callback adds callbacks for that action alone. They run before lifecycle hooks.

        Use lifecycle handlers for request initialization, final headers, and outcome logging.
        Use middleware to wrap downstream work or stop a request, as in the Middleware sample.
        Completion work is awaited before scoped services are disposed. Queue longer jobs to a
        background service using independent data; do not retain request-scoped objects.
        """);

    [Get("/work")]
    public ValueTask<IActionResult> WorkAsync(RequestContext context) => JsonAsync(new
    {
        requestId = _details.Id,
        traceId   = context.TraceId,
    });

    [Get("/callback")]
    public ValueTask<IActionResult> CallbackAsync(RequestContext context)
    {
        // This behavior belongs only to this action, so register it on this response.
        context.Response.OnStarting(() =>
        {
            context.Response.Headers["X-Action"] = "callback";
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("{TraceId}: action starting.", context.TraceId);
            }
            return ValueTask.CompletedTask;
        });
        context.Response.OnCompleted(() =>
        {
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("{TraceId}: action completed.", context.TraceId);
            }
            return ValueTask.CompletedTask;
        });
        return TextAsync("Watch the console for local callbacks running before lifecycle hooks.");
    }

    [Get("/crash")]
    public ValueTask<IActionResult> CrashAsync() => throw new InvalidOperationException("An intentional lifecycle sample failure.");

    [Get("/partial")]
    public async ValueTask<IActionResult> PartialAsync(RequestContext context)
    {
        await context.Response.WriteTextAsync("The response started before the failure.\n", context.RequestAborted);
        throw new InvalidOperationException("The response must now be aborted.");
    }

    [Get("/slow")]
    public async ValueTask<IActionResult> SlowAsync(CancellationToken ct)
    {
        await Task.Delay(Timeout.Infinite, ct);
        return await TextAsync("The request deadline should have canceled this action.");
    }
}
