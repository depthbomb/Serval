#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.Extensions.Logging;
using System.Net;
using System.Text.Json.Serialization;
using Serval.Hosting;
using Serval.Http;
using Serval.Http.Constants;

namespace Serval.Sample.Exceptions;

public sealed class InventoryConflictException : Exception;

public sealed record ErrorEnvelope(int Status, string Code, string Message, string Path, string TraceId);

[JsonSerializable(typeof(ErrorEnvelope))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal partial class ErrorJsonContext : JsonSerializerContext;

// One singleton handles failures from controllers, middleware, result execution,
// routing, and request timeouts. Keep per-request data in local variables.
public sealed class SampleExceptionHandler : IRequestExceptionHandler
{
    private readonly ILogger<SampleExceptionHandler> _logger;

    public SampleExceptionHandler(ILogger<SampleExceptionHandler> logger)
    {
        _logger = logger;
    }

    public async ValueTask HandleAsync(RequestContext context, Exception exception)
    {
        var (status, code, message) = exception switch
        {
            InventoryConflictException => (StatusCodes.Conflict, "inventory_conflict", "The item changed. Reload it and try again."),
            HttpErrorException error   => (error.StatusCode, error.Code ?? "http_error", error.Message),
            _                          => (StatusCodes.InternalServerError, "unexpected_error", "Something went wrong. Use the trace ID when reporting this error."),
        };
        if (status >= 500)
        {
            // Internal details belong in server logs, correlated with the public trace ID.
            _logger.LogError(exception, "Uncaught request failure {TraceId} at {Path}.", context.TraceId, context.Request.Url.AbsolutePath);
        }

        context.Response.Headers.Clear();
        if (exception is HttpErrorException expected)
        {
            // Preserve intentional protocol headers such as Retry-After and Allow.
            foreach (var header in expected.Headers)
            foreach (var value in header.Value)
            {
                context.Response.Headers.Append(header.Key, value);
            }
        }

        context.Response.StatusCode                        = status;
        context.Response.Headers[HeaderNames.CacheControl] = "no-store";
        context.Response.Headers["X-Trace-Id"]             = context.TraceId;
        if (context.Request.Query.TryGetValue("format", out var formats) && formats is ["html"])
        {
            context.Response.Headers[HeaderNames.ContentType] = MimeTypes.Html;
            var html = $"""
                        <!doctype html>
                        <html lang="en">
                        <meta charset="utf-8">
                        <title>Request failed</title>
                        <h1>Request failed ({status})</h1>
                        <p>{WebUtility.HtmlEncode(message)}</p>
                        <p>Trace ID: <code>{WebUtility.HtmlEncode(context.TraceId)}</code></p>
                        </html>
                        """;
            await context.Response.WriteTextAsync(html, CancellationToken.None);
            return;
        }

        context.Response.Headers[HeaderNames.ContentType] = MimeTypes.Json;
        var body = new ErrorEnvelope(status, code, message, context.Request.Url.AbsolutePath, context.TraceId);
        // The request token may already be canceled by a timeout. Serval owns
        // response completion after this handler returns.
        await context.Response.WriteJsonAsync(body, ErrorJsonContext.Default.ErrorEnvelope, CancellationToken.None);
    }
}
