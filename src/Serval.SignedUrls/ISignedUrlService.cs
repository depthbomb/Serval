#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Serval.Http;

namespace Serval.SignedUrls;

/// <summary>Generates signed URLs and validates their readable payloads against incoming URLs.</summary>
/// <remarks>
///     Signatures cover the path, all query values, route metadata, and expiration. They do not bind the host,
///     scheme, HTTP method, or request body. Use a separate key per application and validate before using the data.
///     Tokens are readable and reusable until expiry; they do not encrypt values or provide single-use access.
/// </remarks>
public interface ISignedUrlService
{
    /// <summary>Generates a named route URL and appends its signed payload.</summary>
    /// <param name="routeName">The case-sensitive route name.</param>
    /// <param name="values">Route values formatted using invariant culture.</param>
    /// <param name="query">Query values; entries with null values are omitted by the route generator.</param>
    /// <param name="expiresAt">An optional future expiration time; validation rejects the URL at this instant.</param>
    /// <returns>The escaped application-relative signed URL.</returns>
    /// <exception cref="ArgumentException">
    ///     Route generation fails, normalization changes the route path, the signature
    ///     parameter is used, or the URL is invalid or too long.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">Expiration is not in the future.</exception>
    string Path(string routeName, IReadOnlyDictionary<string, object?>? values = null, IReadOnlyDictionary<string, object?>? query = null, DateTimeOffset? expiresAt = null);

    /// <summary>Generates a named route URL that expires after a positive lifetime.</summary>
    /// <param name="routeName">The case-sensitive route name.</param>
    /// <param name="lifetime">The duration from the service's current UTC time.</param>
    /// <param name="values">Route parameter values.</param>
    /// <param name="query">Optional query values.</param>
    /// <returns>The escaped application-relative signed URL.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The lifetime is not positive or exceeds the supported date range.</exception>
    string TemporaryPath(string routeName, TimeSpan lifetime, IReadOnlyDictionary<string, object?>? values = null, IReadOnlyDictionary<string, object?>? query = null);

    /// <summary>Signs an existing application-relative URL, including a generated typed route helper's output.</summary>
    /// <param name="path">A URL starting with a single slash, without a fragment or existing signature parameter.</param>
    /// <param name="expiresAt">An optional future expiration time.</param>
    /// <returns>The normalized path and query with an appended signed payload; route metadata is empty.</returns>
    /// <exception cref="ArgumentException">The path is invalid, already signed, or too long.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Expiration is not in the future.</exception>
    string Sign(string path, DateTimeOffset? expiresAt = null);

    /// <summary>Validates an application-relative URL or an absolute HTTP(S) URL, ignoring its origin.</summary>
    /// <param name="url">The URL to check. Null and malformed input produce a failed result.</param>
    /// <returns>The validation outcome and, on success only, its authenticated payload.</returns>
    SignedUrlValidationResult Validate(string? url);

    /// <summary>Validates the current request URL.</summary>
    /// <param name="request">The incoming request.</param>
    /// <returns>The validation outcome and verified data.</returns>
    SignedUrlValidationResult Validate(Request request);

    /// <summary>Checks whether a URL passes signature, URL, and expiration validation.</summary>
    /// <param name="url">The application-relative or absolute HTTP(S) URL.</param>
    /// <returns>True only when all checks pass.</returns>
    bool IsValid(string? url);

    /// <summary>Checks whether the current request URL passes validation.</summary>
    /// <param name="request">The incoming request.</param>
    /// <returns>True only when all checks pass.</returns>
    bool IsValid(Request request);
}
