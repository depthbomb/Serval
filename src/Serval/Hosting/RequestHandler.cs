#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Globalization;
using Serval.Http;
using Serval.Http.Constants;
using Serval.Http.StaticFiles;
using Serval.Lifecycle;
using Serval.Middleware;
using Serval.Routing;

namespace Serval.Hosting;

internal sealed class RequestHandler : IRequestHandler
{
    private readonly IRequestExceptionHandler _exceptionHandler;
    private readonly ILogger<RequestHandler>  _logger;
    private readonly ServalOptions            _options;
    private readonly RequestDelegate          _pipeline;
    private readonly Router                   _router;
    private readonly IServiceProvider         _services;
    private readonly StaticFileServer         _staticFiles;
    private readonly Type[]                   _lifecycleHandlers;

    public RequestHandler(IServiceProvider         services,
                          Router                   router,
                          StaticFileServer         staticFiles,
                          IRequestExceptionHandler exceptionHandler,
                          ServalOptions            options,
                          ApplicationPipeline      pipeline,
                          RequestLifecycleRegistry lifecycle,
                          ILogger<RequestHandler>  logger)
    {
        _services          = services;
        _router            = router;
        _staticFiles       = staticFiles;
        _exceptionHandler  = exceptionHandler;
        _options           = options;
        _logger            = logger;
        _pipeline          = pipeline.Build(DispatchAsync);
        _lifecycleHandlers = lifecycle.Freeze();
    }

    public async ValueTask HandleAsync(Request request, Response response, CancellationToken ct)
    {
        var        startedAt = Stopwatch.GetTimestamp();
        Activity?  activity  = null;
        Exception? failure   = null;
        var        reason    = RequestCompletionReason.Completed;
        using var  timeout   = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_options.RequestTimeout);

        AsyncServiceScope?          scope     = null;
        RequestContext?             context   = null;
        string?                     traceId   = null;
        RequestLifecycleInvocation? lifecycle = null;

        request.JsonOptions    = _options.Json;
        request.FormOptions    = _options.Forms;
        request.RequestAborted = timeout.Token;
        response.JsonOptions   = _options.Json;

        try
        {
            try
            {
                activity = ServalDiagnostics.Start(request);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Request diagnostics could not start.");
            }

            traceId = CreateTraceId();
            scope   = _services.CreateAsyncScope();
            context = new RequestContext(request, response, scope.Value.ServiceProvider, _router, traceId, timeout.Token)
            {
                TimeoutSource = timeout,
                StartedAt     = startedAt,
            };

            if (_lifecycleHandlers.Length > 0)
            {
                lifecycle                  = new RequestLifecycleInvocation(context, _lifecycleHandlers);
                response.LifecycleStarting = lifecycle.StartAsync;
                response.ReceivingRequest  = true;
                try
                {
                    await lifecycle.ReceiveAsync(timeout.Token);
                }
                finally
                {
                    response.ReceivingRequest = false;
                }
            }

            await _pipeline(context);
            if (!response.IsAborted)
            {
                await response.CompleteAsync(timeout.Token);
            }
        }
        catch (RequestTransportException exception)
        {
            failure = exception;
            reason  = RequestCompletionReason.TransportError;
            response.EndProcessing(true);
            throw;
        }
        catch (OperationCanceledException exception) when (timeout.IsCancellationRequested)
        {
            failure = exception;
            reason  = ct.IsCancellationRequested ? RequestCompletionReason.Canceled : RequestCompletionReason.TimedOut;
            // A deadline can still become a 408. A disconnect or committed response can only be aborted.
            if (ct.IsCancellationRequested || response.HasStarted)
            {
                response.EndProcessing(true);
                await response.AbortAsync(CancellationToken.None);
                return;
            }

            context ??= CreateFailureContext(request, response, timeout, traceId);
            await HandleExceptionAsync(context, new HttpErrorException(StatusCodes.RequestTimeout, "The request timed out."));
        }
        catch (Exception exception)
        {
            failure =   exception;
            reason  =   RequestCompletionReason.Aborted;
            context ??= CreateFailureContext(request, response, timeout, traceId);

            reason = await HandleExceptionAsync(context, exception);
        }
        finally
        {
            response.EndProcessing();
            if (reason == RequestCompletionReason.Completed && response.IsAborted)
            {
                reason = RequestCompletionReason.Aborted;
            }

            var completion = lifecycle is null ? null : new RequestCompletion(response.StatusCode, response.HasStarted, response.IsAborted,
                Stopwatch.GetElapsedTime(startedAt), reason, failure);
            try
            {
                ServalDiagnostics.Complete(request, response, context, activity, startedAt, failure);
            }
            catch (Exception exception)
            {
                ReportCleanupFailure(exception, "Request diagnostics", context?.TraceId);
            }

            using var completionTimeout = lifecycle is not null || response.HasCompletionCallbacks
                ? new CancellationTokenSource(_options.CompletionTimeout)
                : null;
            var completionToken = completionTimeout?.Token ?? CancellationToken.None;
            await response.RunCompletedAsync(completionToken, exception => ReportCleanupFailure(exception, "Response completion callback", context?.TraceId));
            if (lifecycle is not null)
            {
                await lifecycle.CompleteAsync(completion!, completionToken,
                    exception => ReportCleanupFailure(exception, "Request lifecycle completion hook", context?.TraceId));
            }

            if (response.HasResources)
            {
                await response.DisposeResourcesAsync(exception => ReportCleanupFailure(exception, "Request resource disposal", context?.TraceId));
            }

            if (scope is { } requestScope)
            {
                try
                {
                    await requestScope.DisposeAsync();
                }
                catch (Exception exception)
                {
                    // The response has completed or been aborted. Report cleanup failures
                    // without rewriting it or hiding an earlier transport exception.
                    ReportCleanupFailure(exception, "Request scope disposal", context?.TraceId);
                }
            }

            try
            {
                await request.DisposeAsync();
            }
            catch (Exception exception)
            {
                ReportCleanupFailure(exception, "Request body disposal", context?.TraceId);
            }

            try
            {
                activity?.Dispose();
            }
            catch (Exception exception)
            {
                ReportCleanupFailure(exception, "Request trace disposal", context?.TraceId);
            }
        }
    }

    private async ValueTask DispatchAsync(RequestContext context)
    {
        var request       = context.Request;
        var contentLength = request.Headers[HeaderNames.ContentLength];
        var length        = 0L;
        if (contentLength is not null && !long.TryParse(contentLength, NumberStyles.None, CultureInfo.InvariantCulture, out length))
        {
            throw new HttpErrorException(StatusCodes.BadRequest, "Invalid Content-Length.");
        }

        if (contentLength is not null && request.Headers[HeaderNames.TransferEncoding] is not null)
        {
            throw new HttpErrorException(StatusCodes.BadRequest, "Conflicting request body framing.");
        }

        if (length > _options.MaxRequestBodyBytes)
        {
            throw new HttpErrorException(StatusCodes.PayloadTooLarge, "The request body is too large.");
        }

        if (request.Headers.ExceedsSize(_options.MaxRequestHeaderBytes))
        {
            throw new HttpErrorException(StatusCodes.RequestHeaderFieldsTooLarge);
        }

        if (await RedirectCanonicalPathAsync(context))
        {
            return;
        }

        var served = _options.StaticFiles.Priority == StaticFilePriority.BeforeRouting && await _staticFiles.TryServeAsync(context);
        served = served || await _router.TryDispatchAsync(context);
        served = served || _options.StaticFiles.Priority == StaticFilePriority.AfterRouting && await _staticFiles.TryServeAsync(context);
        served = served || await _staticFiles.TryServeFallbackAsync(context);
        if (!served)
        {
            throw new HttpErrorException(StatusCodes.NotFound, "No route or public file matches this request.");
        }
    }

    private string CreateTraceId()
    {
        var traceId = _options.TraceIdGenerator();
        if (string.IsNullOrWhiteSpace(traceId))
        {
            throw new InvalidOperationException("TraceIdGenerator must return a non-empty, non-whitespace trace ID.");
        }

        return traceId;
    }

    // Keep a successful ID if scope creation failed. A broken generator must not run again during error handling.
    private RequestContext CreateFailureContext(Request request, Response response, CancellationTokenSource timeout, string? traceId)
        => new(request, response, UnavailableRequestServices.Instance, _router, traceId ?? RequestContext.CreateDefaultTraceId(), timeout.Token) { TimeoutSource = timeout };

    private async ValueTask<bool> RedirectCanonicalPathAsync(RequestContext context)
    {
        var path = context.Request.Url.AbsolutePath;
        if (path.StartsWith("//", StringComparison.Ordinal))
        {
            throw new HttpErrorException(StatusCodes.BadRequest, "The request path must start with a single slash.");
        }

        var hasTrailingSlash = path.Length > 1    && path.EndsWith('/');
        var redirect         = path        != "/" && (_options.TrailingSlashes == TrailingSlashBehavior.RedirectToWithoutSlash && hasTrailingSlash || _options.TrailingSlashes == TrailingSlashBehavior.RedirectToWithSlash && !hasTrailingSlash);
        if (!redirect)
        {
            return false;
        }

        var canonical = _options.TrailingSlashes == TrailingSlashBehavior.RedirectToWithSlash ? path + "/" : path.TrimEnd('/');
        await context.Response.RedirectAsync(canonical                                               + context.Request.Url.Query, StatusCodes.PermanentRedirect, context.RequestAborted);
        return true;
    }

    private void ReportCleanupFailure(Exception exception, string stage, string? traceId)
    {
        try
        {
            _logger.LogError(exception, "{Stage} failed for request {TraceId}.", stage, traceId);
        }
        catch
        {
            // A failing logging provider must not interrupt the remaining hooks or request cleanup.
        }
    }

    private async ValueTask<RequestCompletionReason> HandleExceptionAsync(RequestContext context, Exception exception)
    {
        if (context.Response.HasStarted || context.Response.IsAborted)
        {
            context.Response.EndProcessing(true);
            try
            {
                _logger.LogWarning(exception, "Request {TraceId} failed after the response started.", context.TraceId);
            }
            catch
            {
                // Logging failures must not prevent the transport from being aborted.
            }

            await context.Response.AbortAsync(CancellationToken.None);
            return RequestCompletionReason.Aborted;
        }

        try
        {
            // The request token may be the reason we're here. Give the error response a chance to finish.
            await _exceptionHandler.HandleAsync(context, exception);
            await context.Response.CompleteAsync(CancellationToken.None);
        }
        catch (Exception handlerException)
        {
            context.Response.EndProcessing(true);
            ReportCleanupFailure(handlerException, "The exception handler", context.TraceId);
            await context.Response.AbortAsync(CancellationToken.None);
            return handlerException is RequestTransportException ? RequestCompletionReason.TransportError : RequestCompletionReason.Aborted;
        }

        return RequestCompletionReason.Completed;
    }

    private sealed class UnavailableRequestServices : IServiceProvider
    {
        public static readonly UnavailableRequestServices Instance = new();

        // A failed scope must not cause scoped dependencies to be resolved from the root.
        public object GetService(Type serviceType)
            => throw new InvalidOperationException("Request services are unavailable because the request scope could not be created.");
    }
}
