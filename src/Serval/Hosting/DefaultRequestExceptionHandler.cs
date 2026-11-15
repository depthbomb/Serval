#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.Extensions.Logging;
using System.Text.Json.Serialization;
using Serval.Http;
using Serval.Http.Constants;
using Serval.Validation;

namespace Serval.Hosting;

/// <summary>The JSON error body written by the default exception handler.</summary>
/// <param name="Status">The HTTP error status.</param>
/// <param name="Title">A short description of the status.</param>
/// <param name="Detail">An application error message, or null for an unexpected exception.</param>
/// <param name="Code">An optional application error code.</param>
/// <param name="Instance">The request's absolute URL path.</param>
/// <param name="TraceId">The request trace identifier.</param>
public sealed record ProblemDetails(int Status, string Title, string? Detail, string? Code, string Instance, string TraceId)
{
    /// <summary>Field validation messages, omitted for other errors.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, string[]>? Errors { get; init; }
}

[JsonSerializable(typeof(ProblemDetails))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal partial class ErrorJsonContext : JsonSerializerContext;

/// <summary>Writes JSON problem details and logs unexpected request failures.</summary>
/// <remarks>
///     Messages and headers from <see cref="HttpErrorException" /> are exposed to the client. Other exception details stay
///     in the server log.
/// </remarks>
public sealed class DefaultRequestExceptionHandler : IRequestExceptionHandler
{
    private readonly ILogger<DefaultRequestExceptionHandler> _logger;

    /// <summary>Creates an exception handler that logs failures and writes problem responses.</summary>
    /// <param name="logger">The logger used for unexpected exceptions.</param>
    public DefaultRequestExceptionHandler(ILogger<DefaultRequestExceptionHandler> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public async ValueTask HandleAsync(RequestContext context, Exception exception)
    {
        var error      = exception as HttpErrorException;
        var validation = exception as InputValidationException;
        var status     = error?.StatusCode ?? (validation is null ? StatusCodes.InternalServerError : StatusCodes.UnprocessableContent);
        if (error is null && validation is null)
        {
            _logger.LogError(exception, "Unhandled request exception {TraceId}.", context.TraceId);
        }

        context.Response.StatusCode = status;
        // Discard content headers from the failed result before writing a different body.
        context.Response.Headers.Clear();
        context.Response.Headers[HeaderNames.ContentType] = MimeTypes.ProblemJson;

        if (error is not null)
        {
            foreach (var header in error.Headers)
            {
                context.Response.Headers[header.Key] = null;
                foreach (var value in header.Value)
                {
                    context.Response.Headers.Append(header.Key, value);
                }
            }
        }

        var title = status switch
        {
            StatusCodes.BadRequest                  => "Bad Request",
            StatusCodes.Unauthorized                => "Unauthorized",
            StatusCodes.Forbidden                   => "Forbidden",
            StatusCodes.NotFound                    => "Not Found",
            StatusCodes.MethodNotAllowed            => "Method Not Allowed",
            StatusCodes.RequestTimeout              => "Request Timeout",
            StatusCodes.Conflict                    => "Conflict",
            StatusCodes.PayloadTooLarge             => "Payload Too Large",
            StatusCodes.UnsupportedMediaType        => "Unsupported Media Type",
            StatusCodes.UnprocessableContent        => "Unprocessable Content",
            StatusCodes.TooManyRequests             => "Too Many Requests",
            StatusCodes.RequestHeaderFieldsTooLarge => "Request Header Fields Too Large",
            StatusCodes.InternalServerError         => "Internal Server Error",
            StatusCodes.ServiceUnavailable          => "Service Unavailable",
            _                                       => "HTTP Error",
        };

        var problem = new ProblemDetails(status, title, error?.Message ?? validation?.Message, error?.Code ?? (validation is null ? null : "validation_failed"), context.Request.Url.AbsolutePath, context.TraceId)
        {
            Errors = validation?.Errors,
        };
        await context.Response.WriteJsonAsync(problem, ErrorJsonContext.Default.ProblemDetails, CancellationToken.None);
    }
}
