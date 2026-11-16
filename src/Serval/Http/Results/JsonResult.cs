#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using System.Text.Json.Serialization.Metadata;

namespace Serval.Http.Results;

/// <summary>Defers JSON serialization until result execution.</summary>
/// <typeparam name="T">The serialized value type.</typeparam>
public sealed class JsonResult<T> : IActionResult
{
    /// <summary>The value serialized when the result executes.</summary>
    public T Value { get; }

    /// <summary>Explicit serialization metadata, or null to use the configured JSON options.</summary>
    public JsonTypeInfo<T>? TypeInfo { get; }

    /// <summary>The optional status override; null preserves the current response status.</summary>
    public int? StatusCode { get; }

    /// <summary>Creates a deferred JSON result with optional serialization metadata and status.</summary>
    /// <param name="value">The value to serialize.</param>
    /// <param name="typeInfo">Serialization metadata, or null to use the configured JSON options.</param>
    /// <param name="statusCode">An optional status override; null keeps the current response status.</param>
    public JsonResult(T value, JsonTypeInfo<T>? typeInfo = null, int? statusCode = null)
    {
        Value      = value;
        TypeInfo   = typeInfo;
        StatusCode = statusCode;
    }

    /// <inheritdoc />
    public ValueTask ExecuteAsync(RequestContext context)
    {
        if (StatusCode is {} status)
        {
            context.Response.StatusCode = status;
        }

        return TypeInfo is null ? context.Response.WriteJsonAsync(Value, context.RequestAborted) : context.Response.WriteJsonAsync(Value, TypeInfo, context.RequestAborted);
    }
}
