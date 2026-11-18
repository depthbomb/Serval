#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

namespace Serval.Jobs;

/// <summary>A recurring job resolved in a fresh dependency injection scope for each execution.</summary>
public interface IJob
{
    /// <summary>Executes one run. Observe cancellation so the host can shut down promptly.</summary>
    /// <remarks>The job and its scoped dependencies are asynchronously disposed before another run can start.</remarks>
    public ValueTask ExecuteAsync(CancellationToken ct);
}
