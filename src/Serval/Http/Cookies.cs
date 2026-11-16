#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using System.Globalization;
using System.Text;
using Serval.Http.Constants;

namespace Serval.Http;

/// <summary>Controls when a browser includes a cookie on cross-site requests.</summary>
public enum SameSite
{
    /// <summary>Restricts the cookie to same-site requests.</summary>
    Strict,

    /// <summary>Allows same-site requests and qualifying top-level navigations. This is the default.</summary>
    Lax,

    /// <summary>Allows cross-site use and requires the cookie's Secure flag.</summary>
    None,
}

/// <summary>Controls the attributes written with a response cookie.</summary>
public sealed record CookieOptions
{
    /// <summary>The path scope, which must start with a slash. Defaults to /.</summary>
    public string Path { get; init; } = "/";

    /// <summary>The optional domain scope. Null leaves the cookie scoped to the current host.</summary>
    public string? Domain { get; init; }

    /// <summary>The optional absolute expiration, formatted in UTC.</summary>
    public DateTimeOffset? Expires { get; init; }

    /// <summary>The optional lifetime in whole seconds. Negative durations are written as zero.</summary>
    public TimeSpan? MaxAge { get; init; }

    /// <summary>Whether the browser should send the cookie only over HTTPS. Defaults to true.</summary>
    public bool Secure { get; init; } = true;

    /// <summary>Whether the cookie is hidden from client-side scripts. Defaults to true.</summary>
    public bool HttpOnly { get; init; } = true;

    /// <summary>The cross-site cookie policy, defaulting to Lax.</summary>
    public SameSite SameSite { get; init; } = SameSite.Lax;
}

/// <summary>Appends cookie headers to a response.</summary>
public sealed class ResponseCookies
{
    private readonly Headers _headers;

    /// <summary>Creates a cookie writer backed by the response headers.</summary>
    /// <param name="headers">The response headers, which must still be writable.</param>
    public ResponseCookies(Headers headers)
    {
        _headers = headers;
    }

    /// <summary>Appends a Set-Cookie header with a URL-escaped value.</summary>
    /// <param name="name">The cookie's HTTP token name.</param>
    /// <param name="value">The value to URL-escape.</param>
    /// <param name="options">Cookie attributes, or null for the secure, HTTP-only defaults.</param>
    /// <exception cref="ArgumentException">
    ///     The name or attributes are invalid, or SameSite/prefix security requirements are not met.
    /// </exception>
    /// <exception cref="InvalidOperationException">The response headers are already frozen.</exception>
    public void Set(string name, string value, CookieOptions? options = null)
    {
        options ??= new CookieOptions();
        if (!Enum.IsDefined(options.SameSite) || !options.Path.StartsWith('/'))
        {
            throw new ArgumentException("Invalid cookie path or SameSite policy.", nameof(options));
        }

        if (string.IsNullOrEmpty(name) || name.Any(c => !char.IsAsciiLetterOrDigit(c) && !"!#$%&'*+-.^_`|~".Contains(c)))
        {
            throw new ArgumentException("Invalid cookie name.", nameof(name));
        }

        if (options is { SameSite: SameSite.None, Secure: false } ||
            name.StartsWith("__Secure-", StringComparison.Ordinal) &&
            !options.Secure ||
            name.StartsWith("__Host-", StringComparison.Ordinal) &&
            (!options.Secure || options.Domain is not null || options.Path != "/"))
        {
            throw new ArgumentException("Cookie security requirements are not satisfied.", nameof(options));
        }

        ValidateAttribute(options.Path);
        ValidateAttribute(options.Domain);

        var result = new StringBuilder($"{name}={Uri.EscapeDataString(value)}; Path={options.Path}");
        if (options.Domain is not null)
        {
            result.Append("; Domain=").Append(options.Domain);
        }

        if (options.Expires is {} expires)
        {
            result.Append("; Expires=").Append(expires.UtcDateTime.ToString("R", CultureInfo.InvariantCulture));
        }

        if (options.MaxAge is {} maxAge)
        {
            result.Append("; Max-Age=").Append(Math.Max(0, (long)maxAge.TotalSeconds).ToString(CultureInfo.InvariantCulture));
        }

        if (options.Secure)
        {
            result.Append("; Secure");
        }

        if (options.HttpOnly)
        {
            result.Append("; HttpOnly");
        }

        result.Append("; SameSite=").Append(options.SameSite);
        _headers.Append(HeaderNames.SetCookie, result.ToString());
    }

    /// <summary>Expires a cookie by writing an empty value, zero max-age, and a past expiration.</summary>
    /// <param name="name">The cookie name.</param>
    /// <param name="options">Attributes matching the original cookie's path and domain, or null for defaults.</param>
    /// <remarks>The path and domain must match the cookie being removed. Other attributes are retained.</remarks>
    /// <exception cref="ArgumentException">The name or attributes violate cookie validation rules.</exception>
    /// <exception cref="InvalidOperationException">The response headers are already frozen.</exception>
    public void Delete(string name, CookieOptions? options = null)
        => Set(name, "", (options ?? new CookieOptions()) with
        {
            MaxAge = TimeSpan.Zero,
            Expires = DateTimeOffset.UnixEpoch
        });

    private static void ValidateAttribute(string? value)
    {
        if (value?.Any(c => c is < ' ' or >= '\x7f' or ';' or ',') == true)
        {
            throw new ArgumentException("Invalid cookie attribute.");
        }
    }
}
