#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

namespace Serval.Lifecycle;

/// <summary>Describes why server-side request processing ended, independently of the HTTP status.</summary>
public enum RequestCompletionReason
{
    /// <summary>The response finished, including handled HTTP errors and application failures.</summary>
    Completed,

    /// <summary>The transport token was canceled, for example by a disconnect or server shutdown.</summary>
    Canceled,

    /// <summary>The request deadline expired. A 408 response may have been sent.</summary>
    TimedOut,

    /// <summary>The transport reported a request failure.</summary>
    TransportError,

    /// <summary>The response was aborted, for example after an error following response startup.</summary>
    Aborted,
}

/// <summary>A snapshot taken when response processing ends, before completion hooks and resource cleanup.</summary>
/// <remarks>Completion does not guarantee that the client received or consumed the response.</remarks>
public sealed class RequestCompletion
{
    /// <summary>The application's final response status. It may not have reached the client.</summary>
    public int StatusCode { get; }

    /// <summary>Whether response startup or an upgrade was attempted.</summary>
    public bool HasStarted { get; }

    /// <summary>Whether the response was aborted instead of completing normally.</summary>
    public bool IsAborted { get; }

    /// <summary>The time spent processing the request, excluding completion hooks and cleanup.</summary>
    public TimeSpan Elapsed { get; }

    /// <summary>The reason processing ended, independently of StatusCode.</summary>
    public RequestCompletionReason Reason { get; }

    /// <summary>The original request failure, or null. Completion and cleanup failures are logged separately.</summary>
    public Exception? Exception { get; }

    internal RequestCompletion(int statusCode, bool hasStarted, bool isAborted, TimeSpan elapsed, RequestCompletionReason reason, Exception? exception)
    {
        StatusCode = statusCode;
        HasStarted = hasStarted;
        IsAborted  = isAborted;
        Elapsed    = elapsed;
        Reason     = reason;
        Exception  = exception;
    }
}
