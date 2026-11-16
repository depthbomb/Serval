#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

namespace Serval.Http.Results;

/// <summary>Defers writing UTF-8 text until result execution.</summary>
public sealed class TextResult : IActionResult
{
    /// <summary>The text encoded as UTF-8 during execution.</summary>
    public string Text { get; }

    /// <summary>The optional status override; null preserves the current response status.</summary>
    public int? StatusCode { get; }

    /// <summary>Creates a deferred text result with an optional status override.</summary>
    /// <param name="text">The text to write.</param>
    /// <param name="statusCode">An optional status override; null keeps the current response status.</param>
    public TextResult(string text, int? statusCode = null)
    {
        Text       = text;
        StatusCode = statusCode;
    }

    /// <inheritdoc />
    public ValueTask ExecuteAsync(RequestContext context)
    {
        if (StatusCode is {} status)
        {
            context.Response.StatusCode = status;
        }

        return context.Response.WriteTextAsync(Text, context.RequestAborted);
    }
}
