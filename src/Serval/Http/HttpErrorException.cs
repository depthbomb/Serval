#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

namespace Serval.Http;

/// <summary>Ends normal request processing with an HTTP error the client may see.</summary>
public sealed class HttpErrorException : Exception
{
    /// <summary>The error status from 400 through 599.</summary>
    public int StatusCode { get; }

    /// <summary>An optional application-specific code included in problem details.</summary>
    public string? Code { get; }

    /// <summary>Headers copied to the error response by the default exception handler.</summary>
    public Headers Headers { get; } = new();

    /// <summary>Creates an HTTP error with optional client-visible details.</summary>
    /// <param name="statusCode">The error status, from 400 through 599.</param>
    /// <param name="message">A client-visible message, or null to use HTTP followed by the status number.</param>
    /// <param name="code">An optional application error code.</param>
    /// <param name="innerException">The underlying failure, if any.</param>
    /// <exception cref="ArgumentOutOfRangeException">The status is outside 400 through 599.</exception>
    public HttpErrorException(int        statusCode,
                              string?    message        = null,
                              string?    code           = null,
                              Exception? innerException = null) : base(message ?? $"HTTP {statusCode}", innerException)
    {
        if (statusCode is < 400 or > 599)
        {
            throw new ArgumentOutOfRangeException(nameof(statusCode));
        }

        StatusCode = statusCode;
        Code       = code;
    }
}
