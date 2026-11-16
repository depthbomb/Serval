#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

namespace Serval.Http.Results;

/// <summary>Leaves the response unchanged when an action has already handled it.</summary>
public sealed class EmptyResult : IActionResult
{
    /// <summary>The shared stateless empty result.</summary>
    public static EmptyResult Instance { get; } = new();

    private EmptyResult() {}

    /// <inheritdoc />
    public ValueTask ExecuteAsync(RequestContext context) => ValueTask.CompletedTask;
}
