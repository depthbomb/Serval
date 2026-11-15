#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.Extensions.Logging;
using Serval.Lifecycle;

namespace Serval.Sample.Lifecycles;

public sealed class RequestAuditHandler : RequestLifecycleHandler
{
    private readonly RequestDetails               _details;
    private readonly ILogger<RequestAuditHandler> _logger;

    public RequestAuditHandler(RequestDetails details, ILogger<RequestAuditHandler> logger)
    {
        _details = details;
        _logger  = logger;
    }

    public override ValueTask OnRequestCompletedAsync(RequestContext context, RequestCompletion completion, CancellationToken ct)
    {
        _logger.LogInformation("{TraceId}: audit completed request {RequestId}: {Method} {Path}, status {StatusCode}, reason {Reason}, aborted {Aborted}, failure {Failure}.",
            context.TraceId, _details.Id, context.Request.Method, context.Request.Url.AbsolutePath,
            completion.StatusCode, completion.Reason, completion.IsAborted, completion.Exception?.GetType().Name ?? "none");
        return ValueTask.CompletedTask;
    }
}
