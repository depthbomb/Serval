#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using System.Text;

namespace Serval.Savanna;

/// <summary>Builds CSS class lists for templates.</summary>
public static class Css
{
    /// <summary>Combines enabled classes, splits whitespace, and removes duplicate names.</summary>
    /// <param name="values">Class names or conditional class entries, in preferred output order.</param>
    /// <returns>A space-separated list preserving the first occurrence of each case-sensitive name.</returns>
    public static string Classes(params ReadOnlySpan<SavannaClass> values)
    {
        var result = new StringBuilder();
        var seen   = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            if (!value.Enabled || string.IsNullOrWhiteSpace(value.Name))
            {
                continue;
            }

            foreach (var name in value.Name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!seen.Add(name))
                {
                    continue;
                }

                if (result.Length > 0)
                {
                    result.Append(' ');
                }

                result.Append(name);
            }
        }

        return result.ToString();
    }
}

/// <summary>Represents one optional CSS class entry.</summary>
/// <param name="Name">One or more whitespace-separated class names, or null.</param>
/// <param name="Enabled">Whether the names participate in the result; defaults to true.</param>
public readonly record struct SavannaClass(string? Name, bool Enabled = true)
{
    /// <summary>Creates an enabled class entry from a string.</summary>
    /// <param name="name">The class names, or null.</param>
    /// <returns>An enabled class entry.</returns>
    public static implicit operator SavannaClass(string? name) => new(name);

    /// <summary>Creates a class entry with a condition.</summary>
    /// <param name="value">The class names and whether to include them.</param>
    /// <returns>A class entry with the supplied condition.</returns>
    public static implicit operator SavannaClass((string? Name, bool Enabled) value) => new(value.Name, value.Enabled);
}
