#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using System.Buffers;
using System.Collections;

namespace Serval.Http;

/// <summary>Stores case-insensitive HTTP field names with ordered values.</summary>
/// <remarks>Mutations validate field names and values. Freeze makes the collection permanently read-only.</remarks>
public sealed class Headers : IEnumerable<KeyValuePair<string, IReadOnlyList<string>>>
{
    /// <summary>Gets comma-joined values, replaces all values, or removes the header when set to null.</summary>
    /// <param name="name">The case-insensitive header name.</param>
    /// <remarks>
    ///     Returns null when absent. Use GetValues for fields such as Set-Cookie that must remain separate.
    /// </remarks>
    /// <exception cref="ArgumentException">A written name or value contains invalid HTTP characters.</exception>
    /// <exception cref="InvalidOperationException">The collection is frozen and a write is attempted.</exception>
    public string? this[string name]
    {
        get => _values.TryGetValue(name, out var entries) ? string.Join(", ", entries) : null;
        set
        {
            EnsureWritable();
            Validate(name, value);

            if (value is null)
            {
                _values.Remove(name);
            }
            else
            {
                _values[name] = [value];
            }
        }
    }
    private bool _readOnly;

    private readonly        Dictionary<string, List<string>> _values;
    private static readonly SearchValues<char>               TokenCharacters = SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789!#$%&'*+-.^_`|~");

    /// <summary>Creates an empty header collection.</summary>
    public Headers() : this(0) {}

    /// <summary>Creates an empty header collection with room for the expected number of field names.</summary>
    /// <param name="capacity">The expected number of distinct header names.</param>
    /// <exception cref="ArgumentOutOfRangeException">The capacity is negative.</exception>
    public Headers(int capacity)
    {
        _values = new Dictionary<string, List<string>>(capacity, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Enumerates field names and their separate values.</summary>
    /// <returns>An enumerator over read-only value lists.</returns>
    /// <remarks>Do not mutate the headers during enumeration.</remarks>
    public IEnumerator<KeyValuePair<string, IReadOnlyList<string>>> GetEnumerator()
    {
        // Keep the dictionary's struct enumerator and avoid a LINQ iterator here.
        // ReSharper disable once ForeachCanBeConvertedToQueryUsingAnotherGetEnumerator
        foreach (var pair in _values)
        {
            yield return new KeyValuePair<string, IReadOnlyList<string>>(pair.Key, pair.Value.AsReadOnly());
        }
    }

    /// <summary>Adds a separate value without replacing existing values.</summary>
    /// <param name="name">The case-insensitive header name.</param>
    /// <param name="value">The field value to append.</param>
    /// <exception cref="ArgumentException">The name or value contains invalid HTTP characters.</exception>
    /// <exception cref="InvalidOperationException">The collection is frozen.</exception>
    public void Append(string name, string value)
    {
        EnsureWritable();
        Validate(name, value);

        if (!_values.TryGetValue(name, out var entries))
        {
            _values.Add(name, entries = []);
        }

        entries.Add(value);
    }

    /// <summary>Retrieves the separate values for a header.</summary>
    /// <param name="name">The case-insensitive header name.</param>
    /// <returns>A read-only view of the existing values, or an empty list when absent.</returns>
    /// <remarks>The view is not a snapshot; appending to the same underlying entry can change it.</remarks>
    public IReadOnlyList<string> GetValues(string name) => _values.TryGetValue(name, out var entries) ? entries.AsReadOnly() : [];

    /// <summary>Permanently prevents changes to this collection.</summary>
    public void Freeze() => _readOnly = true;

    /// <summary>Removes every header.</summary>
    /// <exception cref="InvalidOperationException">The collection is frozen.</exception>
    public void Clear()
    {
        EnsureWritable();
        _values.Clear();
    }

    internal bool ExceedsSize(int maximum)
    {
        long total = 0;
        foreach (var header in _values)
        {
            total += header.Key.Length;
            foreach (var value in header.Value)
            {
                total += value.Length;
                if (total > maximum)
                {
                    return true;
                }
            }
        }

        return false;
    }

    internal static void ValidateTrailer(string name, string value)
    {
        Validate(name, value);
        // Trailers must not redefine framing, routing, or connection fields after the request was accepted.
        var forbidden = name.AsSpan().ContainsAnyInRange('A', 'Z') || name is "content-length" or "host" or "connection" or "transfer-encoding" or "keep-alive" or "upgrade" or "proxy-connection" or "te";
        if (forbidden || value.Length > 0 && (value[0] is ' ' or '\t' || value[^1] is ' ' or '\t'))
        {
            throw new ArgumentException("Invalid trailer field.");
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private void EnsureWritable()
    {
        if (_readOnly)
        {
            throw new InvalidOperationException("Headers have already been committed.");
        }
    }

    private static void Validate(string name, string? value)
    {
        if (string.IsNullOrEmpty(name) || name.AsSpan().ContainsAnyExcept(TokenCharacters))
        {
            throw new ArgumentException("Invalid HTTP header name.", nameof(name));
        }

        // Reject control characters before a field reaches any transport to prevent response splitting.
        if (
            value.AsSpan().ContainsAnyInRange('\0', '\b')   ||
            value.AsSpan().ContainsAnyInRange('\n', '\x1f') ||
            value.AsSpan().Contains('\x7f')
        )
        {
            throw new ArgumentException("Invalid HTTP header value.", nameof(value));
        }
    }
}
