#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

namespace Serval.Routing;

/// <summary>An application policy for URLs written into HTML attributes.</summary>
public interface IUrlPolicy
{
    /// <summary>Decides whether Savanna may write a URL into an HTML attribute.</summary>
    /// <param name="url">The unencoded URL to check.</param>
    /// <returns>True to permit the URL; false to reject it.</returns>
    /// <remarks>
    ///     Savanna calls this policy for string, Uri, and SavannaUrl values. String and Uri writes do not run SavannaUrl.From
    ///     validation, so check schemes and destinations here when the application requires it.
    /// </remarks>
    bool IsAllowed(string url);
}
