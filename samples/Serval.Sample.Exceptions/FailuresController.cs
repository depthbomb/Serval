#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Serval.Http;
using Serval.Http.Constants;
using Serval.Middleware;
using Serval.Routing;

namespace Serval.Sample.Exceptions;

[Controller]
public sealed class FailuresController : ControllerBase
{
    [Get("/")]
    public ValueTask<IActionResult> IndexAsync(RequestContext context) => TextAsync("""
                                                                                    Centralized uncaught exception handling
                                                                                    GET /crash       Controller throws -> safe 500 JSON and server-side exception log
                                                                                    GET /middleware  Middleware throws -> the same central handler
                                                                                    GET /result      Deferred IActionResult throws -> the same central handler
                                                                                    GET /conflict    Application exception mapped centrally to 409
                                                                                    GET /limited     Intentional HttpErrorException -> 429 with Retry-After
                                                                                    GET /slow        Cancellation-aware work exceeds 500 ms -> 408
                                                                                    GET /missing     No matching route -> 404 in the same error format
                                                                                    GET /partial     Throws after flushing a response; Serval aborts the stream.
                                                                                    Add ?format=html to any error route to get an HTML error page from the same handler.
                                                                                    The handler cannot replace headers or append a JSON error once a response starts.
                                                                                    Check X-Trace-Id and the JSON traceId to correlate errors with server logs.
                                                                                    Remove the custom handler registration to use Serval's default ProblemDetails.
                                                                                    """);

    [Get("/crash")]
    public ValueTask<IActionResult> CrashAsync(RequestContext context)
        => throw new InvalidOperationException("Simulated database failure (server logs only).");

    [Get("/middleware")]
    [UseMiddleware(typeof(FailingMiddleware))]
    public ValueTask<IActionResult> MiddlewareAsync(RequestContext context) => TextAsync("This action is never reached.");

    [Get("/result")]
    public ValueTask<IActionResult> ResultAsync(RequestContext context) => ValueTask.FromResult<IActionResult>(new FailingResult());

    [Get("/conflict")]
    public ValueTask<IActionResult> ConflictAsync(RequestContext context) => throw new InventoryConflictException();

    [Get("/limited")]
    public ValueTask<IActionResult> LimitedAsync(RequestContext context)
    {
        var error = new HttpErrorException(StatusCodes.TooManyRequests, "Try again in 30 seconds.", "rate_limited")
        {
            Headers =
            {
                ["Retry-After"] = "30",
            },
        };
        throw error;
    }

    [Get("/slow")]
    public async ValueTask<IActionResult> SlowAsync(RequestContext context, CancellationToken cancellationToken)
    {
        // The generator supplies context.RequestAborted to CancellationToken parameters.
        await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
        return Text("The timeout should cancel this work first.");
    }

    [Get("/partial")]
    public async ValueTask<IActionResult> PartialAsync(RequestContext context)
    {
        await context.Response.WriteTextAsync("The response has already started.\n", context.RequestAborted);
        await context.Response.FlushAsync(context.RequestAborted);
        throw new InvalidOperationException("Simulated failure after response commit.");
    }
}

public sealed class FailingMiddleware : IMiddleware
{
    public ValueTask InvokeAsync(RequestContext context, RequestDelegate next)
        => throw new InvalidOperationException("Simulated middleware failure (server logs only).");
}

public sealed class FailingResult : IActionResult
{
    public ValueTask ExecuteAsync(RequestContext context)
        => throw new InvalidOperationException("Simulated result failure (server logs only).");
}
