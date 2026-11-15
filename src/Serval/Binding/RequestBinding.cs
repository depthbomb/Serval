#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using System.ComponentModel;
using System.Globalization;
using Serval.Http;
using Serval.Http.Constants;

namespace Serval.Binding;

/// <summary>Shared parsing operations used by generated action wrappers.</summary>
/// <remarks>Application code normally uses binding attributes instead of calling these methods directly.</remarks>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class RequestBinding
{
    /// <summary>Reads a scalar route, query, header, or previously parsed form value.</summary>
    /// <param name="context">The current request.</param>
    /// <param name="source">One of Route, Query, Header, or Form.</param>
    /// <param name="name">The value's name in that source.</param>
    /// <returns>The value, or null when absent. Empty strings remain empty.</returns>
    /// <exception cref="HttpErrorException">A query or header has multiple values; the status is 400.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The source is unsupported.</exception>
    public static string? Read(RequestContext context, string source, string name)
    {
        if (source == "Route")
        {
            return context.RouteValues.GetValueOrDefault(name);
        }

        var values = ReadMany(context, source, name);
        if (values.Count > 1)
        {
            throw Invalid(source, name);
        }

        return values.Count == 0 ? null : values[0];
    }

    /// <summary>Reads the separate values of a query parameter, header, or previously parsed form field.</summary>
    /// <param name="context">The current request.</param>
    /// <param name="source">Query, Header, or Form.</param>
    /// <param name="name">The value's name in that source.</param>
    /// <returns>Values in their original order, or an empty list when absent.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The source is unsupported.</exception>
    public static IReadOnlyList<string> ReadMany(RequestContext context, string source, string name) => source switch
    {
        "Query"  => context.Request.Query.TryGetValue(name, out var values) ? values : [],
        "Header" => context.Request.Headers.GetValues(name),
        "Form"   => context.Request.ParsedFormValues(name),
        _        => throw new ArgumentOutOfRangeException(nameof(source)),
    };

    /// <summary>Requires a string value, allowing an empty string.</summary>
    /// <param name="value">The input, or null when absent.</param>
    /// <param name="source">The source used in an error message.</param>
    /// <param name="name">The value's name used in an error message.</param>
    /// <returns>The supplied string.</returns>
    /// <exception cref="HttpErrorException">The value is absent; the status is 400.</exception>
    public static string Require(string? value, string source, string name) => value ?? throw Invalid(source, name);

    /// <summary>Parses a required scalar using invariant culture.</summary>
    /// <typeparam name="T">The target type implementing its own string parser.</typeparam>
    /// <param name="value">The input, or null when absent.</param>
    /// <param name="source">The source used in an error message.</param>
    /// <param name="name">The value's name used in an error message.</param>
    /// <returns>The parsed value.</returns>
    /// <exception cref="HttpErrorException">The value is absent or invalid; the status is 400.</exception>
    public static T Parse<T>(string? value, string source, string name) where T : IParsable<T>
        => value is not null && T.TryParse(value, CultureInfo.InvariantCulture, out var parsed) ? parsed : throw Invalid(source, name);

    /// <summary>Parses an enum name, comma-separated names, or a numeric value.</summary>
    /// <typeparam name="T">The enum type.</typeparam>
    /// <param name="value">The input, or null when absent. Names are case-insensitive.</param>
    /// <param name="source">The source used in an error message.</param>
    /// <param name="name">The value's name used in an error message.</param>
    /// <returns>The parsed enum value. Numeric values need not have a declared enum member.</returns>
    /// <exception cref="HttpErrorException">The value is absent or invalid; the status is 400.</exception>
    public static T ParseEnum<T>(string? value, string source, string name) where T : struct, Enum
        => value is not null && Enum.TryParse<T>(value, true, out var parsed) ? parsed : throw Invalid(source, name);

    private static HttpErrorException Invalid(string source, string name)
        => new(StatusCodes.BadRequest, $"{source} parameter '{name}' is missing, repeated, or invalid.");
}
