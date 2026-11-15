#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

namespace Serval.Savanna;

/// <summary>Marks application-trusted HTML that may be written without escaping.</summary>
/// <remarks>This type does not sanitize markup. Only create it from content the application already trusts.</remarks>
public readonly struct SavannaHtml
{
    /// <summary>The trusted markup supplied at creation; a default struct has no value.</summary>
    public string Value { get; }

    private SavannaHtml(string value)
    {
        Value = value;
    }

    /// <summary>Marks markup as trusted without validating or sanitizing it.</summary>
    /// <param name="html">The application-trusted HTML.</param>
    /// <returns>A value that Savanna can write without HTML escaping.</returns>
    /// <exception cref="ArgumentNullException">The markup is null.</exception>
    public static SavannaHtml Trusted(string html) => new(html ?? throw new ArgumentNullException(nameof(html)));

    /// <summary>Returns the trusted markup as plain text.</summary>
    /// <returns>The markup, or an empty string for a default value.</returns>
    public override string ToString() => Value ?? "";
}

/// <summary>Holds a URL validated by Savanna's built-in scheme and character checks.</summary>
public readonly struct SavannaUrl
{
    /// <summary>The validated URL text; a default struct has no value.</summary>
    public string Value { get; }

    private SavannaUrl(string value)
    {
        Value = value;
    }

    /// <summary>Returns the validated URL text.</summary>
    /// <returns>The URL, or an empty string for a default value.</returns>
    public override string ToString() => Value ?? "";

    /// <summary>Validates an absolute or relative URL for use in a view.</summary>
    /// <param name="value">The URL text; an empty relative URL is allowed.</param>
    /// <returns>The validated URL.</returns>
    /// <remarks>
    ///     Allows http, https, mailto, and tel absolute URLs. Rejects whitespace, control characters, backslashes,
    ///     network-path references, and relative first segments containing a colon. An application URL policy can impose
    ///     additional checks when writing.
    /// </remarks>
    /// <exception cref="ArgumentNullException">The URL is null.</exception>
    /// <exception cref="ArgumentException">The URL uses a forbidden scheme or form.</exception>
    public static SavannaUrl From(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (value.Any(character => char.IsControl(character) || char.IsWhiteSpace(character) || character == '\\') || value.StartsWith("//", StringComparison.Ordinal))
        {
            throw new ArgumentException("A view URL cannot contain whitespace, control characters, backslashes, or a network-path reference.", nameof(value));
        }

        // A leading slash is site-relative here, even when Uri sees a Unix file path.
        if (!value.StartsWith('/') && Uri.TryCreate(value, UriKind.Absolute, out var absolute))
        {
            if (absolute.Scheme is not ("http" or "https" or "mailto" or "tel"))
            {
                throw new ArgumentException("The view URL uses an unsupported scheme.", nameof(value));
            }
        }
        else
        {
            var firstSegment = value.Split('/', '?', '#')[0];
            if (firstSegment.Contains(':'))
            {
                throw new ArgumentException("The view URL is not a valid relative URL.", nameof(value));
            }
        }

        return new SavannaUrl(value);
    }
}
