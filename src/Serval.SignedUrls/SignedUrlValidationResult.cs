#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using System.Collections.ObjectModel;

namespace Serval.SignedUrls;

/// <summary>Describes why a signed URL was rejected.</summary>
public enum SignedUrlFailure
{
    /// <summary>The URL passed all checks.</summary>
    None,
    /// <summary>The URL does not contain a signature parameter.</summary>
    MissingSignature,
    /// <summary>The URL, token, or payload is malformed, duplicated, unsupported, or too large.</summary>
    Malformed,
    /// <summary>The token does not have a valid signature for this application's key.</summary>
    InvalidSignature,
    /// <summary>The path or query values differ from the signed payload.</summary>
    UrlMismatch,
    /// <summary>The signed expiration time has been reached.</summary>
    Expired,
}

/// <summary>The result of signature, URL, and expiration checks.</summary>
public sealed class SignedUrlValidationResult
{
    /// <summary>True only when all checks passed.</summary>
    public bool IsValid => Failure == SignedUrlFailure.None;

    /// <summary>The reason validation failed, or None on success.</summary>
    public SignedUrlFailure Failure { get; }

    /// <summary>Verified data, available only on successful validation.</summary>
    public SignedUrlData? Data { get; }

    internal SignedUrlValidationResult(SignedUrlFailure failure, SignedUrlData? data = null)
    {
        Failure = failure;
        Data    = data;
    }
}

/// <summary>Immutable data authenticated by a successfully validated signed URL.</summary>
public sealed class SignedUrlData
{
    /// <summary>The escaped application-relative path, excluding the query string.</summary>
    public string Path { get; }

    /// <summary>The name used to generate the route, or null when an existing path was signed.</summary>
    public string? RouteName { get; }

    /// <summary>The supplied route values formatted with invariant culture. Defaults remain represented in Path.</summary>
    public IReadOnlyDictionary<string, string?> RouteValues { get; }

    /// <summary>Decoded query values, excluding the signature. Names ignore case and repeated values retain order.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Query { get; }

    /// <summary>The exclusive UTC expiration time, or null when the URL does not expire.</summary>
    public DateTimeOffset? ExpiresAt { get; }

    internal SignedUrlData(string path, string? routeName, Dictionary<string, string?> routeValues, IEnumerable<KeyValuePair<string, string[]>> query, DateTimeOffset? expiresAt)
    {
        Path        = path;
        RouteName   = routeName;
        RouteValues = new ReadOnlyDictionary<string, string?>(routeValues);
        Query       = new ReadOnlyDictionary<string, IReadOnlyList<string>>(query.ToDictionary(pair => pair.Key, IReadOnlyList<string> (pair) => Array.AsReadOnly(pair.Value), StringComparer.OrdinalIgnoreCase));
        ExpiresAt   = expiresAt;
    }
}
