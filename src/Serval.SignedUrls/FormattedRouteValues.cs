#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using System.Collections;
using System.Globalization;

namespace Serval.SignedUrls;

// Keep the caller's key lookup semantics while formatting each object once for both routing and the payload.
internal sealed class FormattedRouteValues : IReadOnlyDictionary<string, object?>
{
    public int                  Count  => _values.Count;
    public IEnumerable<string>  Keys   => _values.Keys;
    public IEnumerable<object?> Values => _values.Values.Select(Format);
    public object? this[string key] => Format(_values[key]);

    private Dictionary<object, string?>? _formatted;

    private readonly IReadOnlyDictionary<string, object?> _values;

    internal FormattedRouteValues(IReadOnlyDictionary<string, object?> values)
    {
        _values = values;
    }

    public bool ContainsKey(string key) => _values.ContainsKey(key);

    public bool TryGetValue(string key, out object? value)
    {
        if (!_values.TryGetValue(key, out var original))
        {
            value = null;
            return false;
        }

        value = Format(original);
        return true;
    }

    public IEnumerator<KeyValuePair<string, object?>> GetEnumerator()
    {
        return _values.Select(pair => new KeyValuePair<string, object?>(pair.Key, Format(pair.Value))).GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private string? Format(object? value)
    {
        if (value is null or string)
        {
            return (string?)value;
        }

        _formatted ??= new Dictionary<object, string?>(ReferenceEqualityComparer.Instance);
        if (!_formatted.TryGetValue(value, out var formatted))
        {
            formatted = Convert.ToString(value, CultureInfo.InvariantCulture);
            _formatted.Add(value, formatted);
        }

        return formatted;
    }
}
