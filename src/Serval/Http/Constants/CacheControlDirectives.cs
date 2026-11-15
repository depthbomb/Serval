#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

namespace Serval.Http.Constants;

/// <summary>Common Cache-Control directives, ready to combine with comma separators.</summary>
/// <remarks>Directives requiring a value contain only the name, such as MaxAge; append <c>=</c> and the value.</remarks>
public static class CacheControlDirectives
{
    /// <summary>Allows storage but requires successful validation before reuse.</summary>
    public const string NoCache = "no-cache";

    /// <summary>Prevents caches from storing the response.</summary>
    public const string NoStore = "no-store";

    /// <summary>Allows storage in a private cache, such as the user's browser.</summary>
    public const string Private = "private";

    /// <summary>Allows storage in shared caches when other caching requirements are met.</summary>
    public const string Public = "public";

    /// <summary>Requires successful validation before a stale response can be reused.</summary>
    public const string MustRevalidate = "must-revalidate";

    /// <summary>Applies the stale-response validation requirement to shared caches.</summary>
    public const string ProxyRevalidate = "proxy-revalidate";

    /// <summary>Prevents intermediaries from transforming the representation.</summary>
    public const string NoTransform = "no-transform";

    /// <summary>Indicates that the representation will not change during its freshness lifetime.</summary>
    public const string Immutable = "immutable";

    /// <summary>The maximum freshness age in seconds; append a value such as <c>=3600</c>.</summary>
    public const string MaxAge = "max-age";

    /// <summary>The shared-cache freshness age in seconds; append a value such as <c>=3600</c>.</summary>
    public const string SharedMaxAge = "s-maxage";

    /// <summary>The time in seconds stale data may be served while it is revalidated; append a value.</summary>
    public const string StaleWhileRevalidate = "stale-while-revalidate";

    /// <summary>The time in seconds stale data may be served after an error; append a value.</summary>
    public const string StaleIfError = "stale-if-error";
}
