#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Serval.Binding;

namespace Serval.Http;

public sealed partial class Request
{
    /// <summary>Gets exactly one required query string, allowing an empty value. Missing or repeated values return 400.</summary>
    public string QueryValue(string name) => QueryValue<string>(name);

    /// <summary>Gets a query scalar, using the fallback only when the parameter is absent.</summary>
    public T QueryValue<T>(string name, T fallback) where T : IParsable<T> => Query.ContainsKey(name) ? QueryValue<T>(name) : fallback;

    /// <summary>Tries to parse exactly one query value using invariant culture.</summary>
    public bool TryQueryValue<T>(string name, [MaybeNullWhen(false)] out T value) where T : IParsable<T>
    {
        value = default;
        return Query.TryGetValue(name, out var values) && values.Length == 1 && T.TryParse(values[0], CultureInfo.InvariantCulture, out value);
    }

    /// <summary>Gets repeated query values in order, or an empty array when absent. Invalid values return 400.</summary>
    public T[] QueryValues<T>(string name) where T : IParsable<T>
        => Query.TryGetValue(name, out var values) ? [.. values.Select(value => RequestBinding.Parse<T>(value, "Query", name))] : [];

    /// <summary>Gets a required query enum. Names ignore case; repeated or invalid values return 400.</summary>
    public T QueryEnum<T>(string name) where T : struct, Enum => RequestBinding.ParseEnum<T>(QueryValue(name), "Query", name);
}
