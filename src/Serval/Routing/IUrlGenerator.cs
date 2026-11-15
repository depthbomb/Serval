#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

namespace Serval.Routing;

/// <summary>Generates application-relative paths for named routes.</summary>
public interface IUrlGenerator
{
    /// <summary>Expands a named route and appends optional query values.</summary>
    /// <param name="routeName">The case-sensitive route name.</param>
    /// <param name="values">Parameter values formatted with invariant culture; null supplies no values.</param>
    /// <param name="query">Query values to escape and append; entries with null values are omitted.</param>
    /// <returns>An escaped application-relative path, with a query string when supplied.</returns>
    /// <exception cref="ArgumentException">
    ///     The route is unknown, or required values are missing or fail the route's constraints.
    /// </exception>
    string Path(string routeName, IReadOnlyDictionary<string, object?>? values = null, IReadOnlyDictionary<string, object?>? query = null);
}
