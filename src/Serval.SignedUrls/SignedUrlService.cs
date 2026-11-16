#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using System.Buffers;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Serval.Http;
using Serval.Routing;

namespace Serval.SignedUrls;

/// <summary>A thread-safe URL signer using versioned JSON payloads and HMAC-SHA256.</summary>
/// <remarks>Uses the same origin-independent signing contract as <see cref="ISignedUrlService" />.</remarks>
public sealed class SignedUrlService : ISignedUrlService
{
    private readonly TimeProvider  _clock;
    private readonly byte[]        _key;
    private readonly int           _maxUrlLength;
    private readonly string        _parameterName;
    private readonly IUrlGenerator _urls;

    /// <summary>Creates a signer and takes a private copy of its configuration and key.</summary>
    /// <param name="urls">The application's route URL generator.</param>
    /// <param name="options">The signing key, reserved parameter name, and URL limit.</param>
    /// <param name="clock">The clock used for expiration; defaults to the system clock.</param>
    /// <exception cref="ArgumentException">The configuration is invalid.</exception>
    public SignedUrlService(IUrlGenerator urls, SignedUrlOptions options, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(urls);
        ArgumentNullException.ThrowIfNull(options);

        options.Validate();

        _urls          = urls;
        _key           = [.. options.SigningKey];
        _parameterName = options.SignatureParameterName;
        _maxUrlLength  = options.MaxUrlLength;
        _clock         = clock ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public string Path(string                                routeName,
                       IReadOnlyDictionary<string, object?>? values    = null,
                       IReadOnlyDictionary<string, object?>? query     = null,
                       DateTimeOffset?                       expiresAt = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(routeName);

        var formatted   = values is null ? null : new FormattedRouteValues(values);
        var path        = _urls.Path(routeName, formatted, query);
        var routeValues = formatted?.ToDictionary(pair => pair.Key, pair => (string?)pair.Value, StringComparer.Ordinal) ?? [];
        return SignCore(path, routeName, routeValues, expiresAt);
    }

    /// <inheritdoc />
    public string TemporaryPath(string                                routeName,
                                TimeSpan                              lifetime,
                                IReadOnlyDictionary<string, object?>? values = null,
                                IReadOnlyDictionary<string, object?>? query  = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lifetime, TimeSpan.Zero);

        return Path(routeName, values, query, _clock.GetUtcNow().Add(lifetime));
    }

    /// <inheritdoc />
    public string Sign(string path, DateTimeOffset? expiresAt = null) => SignCore(path, null, [], expiresAt);

    /// <inheritdoc />
    public SignedUrlValidationResult Validate(string? url)
    {
        var failure = ValidateCore(url, true, out var data);
        return new SignedUrlValidationResult(failure, data);
    }

    /// <inheritdoc />
    public SignedUrlValidationResult Validate(Request request)
    {
        var failure = ValidateCore(request, true, out var data);
        return new SignedUrlValidationResult(failure, data);
    }

    /// <inheritdoc />
    public bool IsValid(string? url) => ValidateCore(url, false, out _) == SignedUrlFailure.None;

    /// <inheritdoc />
    public bool IsValid(Request request) => ValidateCore(request, false, out _) == SignedUrlFailure.None;

    private SignedUrlFailure ValidateCore(string? url, bool includeData, out SignedUrlData? data)
    {
        data = null;
        return SignedUrlEncoding.TryParseUrl(url, _maxUrlLength, false, out var uri) ? ValidateUri(uri, includeData, out data) : SignedUrlFailure.Malformed;
    }

    private SignedUrlFailure ValidateCore(Request request, bool includeData, out SignedUrlData? data)
    {
        ArgumentNullException.ThrowIfNull(request);

        data = null;
        // Use exactly the path the router sees, including for custom transports that disable Uri normalization.
        return SignedUrlEncoding.IsValidUri(request.Url, _maxUrlLength) ? ValidateUri(request.Url, includeData, out data) : SignedUrlFailure.Malformed;
    }

    private SignedUrlFailure ValidateUri(Uri uri, bool includeData, out SignedUrlData? data)
    {
        data = null;

        var query = SignedUrlEncoding.ParseQuery(uri.Query);
        if (!query.Remove(_parameterName, out var tokens))
        {
            return SignedUrlFailure.MissingSignature;
        }

        if (tokens.Length != 1)
        {
            return SignedUrlFailure.Malformed;
        }

        var token     = tokens[0].AsSpan();
        var separator = token.IndexOf('.');
        if (separator <= 0 || token.Length - separator - 1 != 43)
        {
            return SignedUrlFailure.Malformed;
        }

        var        encoded   = token[..separator];
        Span<byte> signature = stackalloc byte[32];
        Span<byte> expected  = stackalloc byte[32];
        if (!SignedUrlEncoding.TryDecodeToken(token[(separator + 1)..], signature) || !SignedUrlEncoding.IsCanonicalToken(encoded))
        {
            return SignedUrlFailure.Malformed;
        }

        SignPayload(encoded, expected);
        if (!CryptographicOperations.FixedTimeEquals(signature, expected))
        {
            return SignedUrlFailure.InvalidSignature;
        }

        SignedUrlInput? payload;
        try
        {
            // Never deserialize or expose claims until their signature has been verified.
            payload = JsonSerializer.Deserialize(Base64Url.DecodeFromChars(encoded), SignedUrlJsonContext.Default.SignedUrlInput);
        }
        catch (JsonException)
        {
            return SignedUrlFailure.Malformed;
        }

        if (payload is null                                                                              ||
            payload.Version != 1                                                                         ||
            payload.Path is null                                                                         ||
            payload.RouteValues is null                                                                  ||
            payload.Query is null                                                                        ||
            payload.Query.Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() != payload.Query.Count ||
            payload.Query.Values.Any(values => values is null || values.Any(value => value is null)))
        {
            return SignedUrlFailure.Malformed;
        }

        if (!string.Equals(payload.Path, uri.AbsolutePath, StringComparison.Ordinal) ||
            payload.Query.Count != query.Count                                       ||
            payload.Query.Any(pair => !query.TryGetValue(pair.Key, out var actual) || pair.Value is null || !pair.Value.SequenceEqual(actual, StringComparer.Ordinal)))
        {
            return SignedUrlFailure.UrlMismatch;
        }

        if (payload.ExpiresAt is {} expiresAt && _clock.GetUtcNow() >= expiresAt)
        {
            return SignedUrlFailure.Expired;
        }

        if (includeData)
        {
            // Preserve the signed names while reusing the parsed, non-null values that matched them.
            var verifiedQuery = payload.Query.Select(pair => KeyValuePair.Create(pair.Key, query[pair.Key]));
            data = new SignedUrlData(payload.Path, payload.RouteName, payload.RouteValues, verifiedQuery, payload.ExpiresAt);
        }

        return SignedUrlFailure.None;
    }

    private string SignCore(string path, string? routeName, Dictionary<string, string?> routeValues, DateTimeOffset? expiresAt)
    {
        if (!SignedUrlEncoding.TryParseUrl(path, _maxUrlLength, true, out var uri))
        {
            throw new ArgumentException("Signing requires an application-relative URL starting with a single slash, without a fragment, and within the URL limit.", nameof(path));
        }

        var queryStart = path.IndexOf('?');
        var routePath  = queryStart < 0 ? path.AsSpan() : path.AsSpan(0, queryStart);
        if (routeName is not null && !routePath.SequenceEqual(uri.AbsolutePath))
        {
            throw new ArgumentException("The generated route path changes during URL normalization. Route values must not introduce dot segments.", nameof(path));
        }

        if (expiresAt is {} expiration && expiration <= _clock.GetUtcNow())
        {
            throw new ArgumentOutOfRangeException(nameof(expiresAt), "Expiration must be in the future.");
        }

        var query = SignedUrlEncoding.ParseQuery(uri.Query);
        if (query.ContainsKey(_parameterName))
        {
            throw new ArgumentException($"Query parameter '{_parameterName}' is reserved for the signature.", nameof(path));
        }

        var payload = new SignedUrlPayload
        {
            Version     = 1,
            Path        = uri.AbsolutePath,
            RouteName   = routeName,
            RouteValues = routeValues,
            Query       = query,
            ExpiresAt   = expiresAt?.ToUniversalTime(),
        };
        var        encoded   = SignedUrlEncoding.Encode(JsonSerializer.SerializeToUtf8Bytes(payload, SignedUrlJsonContext.Default.SignedUrlPayload));
        Span<byte> signature = stackalloc byte[32];

        SignPayload(encoded, signature);

        var signed = uri.PathAndQuery + (uri.Query.Length == 0 ? "?" : "&") + _parameterName + "=" + encoded + "." + SignedUrlEncoding.Encode(signature);
        if (signed.Length > _maxUrlLength)
        {
            throw new ArgumentException("The signed URL exceeds the configured maximum length.", nameof(path));
        }

        return signed;
    }

    private void SignPayload(ReadOnlySpan<char> encoded, Span<byte> signature)
    {
        var     prefix = "Serval.SignedUrls.v1."u8;
        var     length = prefix.Length + encoded.Length;
        byte[]? rented = null;
        var     input  = length <= 1_024 ? stackalloc byte[length] : rented = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            prefix.CopyTo(input);
            Encoding.ASCII.GetBytes(encoded, input[prefix.Length..]);
            HMACSHA256.HashData(_key, input[..length], signature);
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<byte>.Shared.Return(rented, true);
            }
        }
    }
}
