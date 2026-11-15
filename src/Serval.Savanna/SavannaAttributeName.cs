#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using System.Text;

namespace Serval.Savanna;

// Compiled views cache these once so attribute names need no encoding during a render.
/// <summary>Validates and caches the UTF-8 prefix of an HTML attribute.</summary>
/// <remarks>
///     Generated views reuse these instances to avoid encoding attribute names on every render. This validates name
///     characters, not whether an attribute is safe for untrusted content.
/// </remarks>
public sealed class SavannaAttributeName
{
    /// <summary>The validated attribute name in its original spelling.</summary>
    public string Name { get; }

    internal byte[] Prefix        { get; }
    internal byte[] BooleanPrefix { get; }
    internal bool   IsHidden      { get; }

    /// <summary>Creates the reusable encoded prefixes for an attribute name.</summary>
    /// <param name="name">
    ///     A nonempty name containing ASCII letters, digits, hyphens, underscores, colons, or dots.
    /// </param>
    /// <exception cref="ArgumentException">The name is empty or contains an unsupported character.</exception>
    public SavannaAttributeName(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        foreach (var character in name)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_' or ':' or '.'))
            {
                throw new ArgumentException("Invalid HTML attribute name.", nameof(name));
            }
        }

        Name          = name;
        Prefix        = Encoding.UTF8.GetBytes(" " + name + "=\"");
        BooleanPrefix = Encoding.UTF8.GetBytes(" " + name);
        IsHidden      = name.Equals("hidden", StringComparison.OrdinalIgnoreCase);
    }
}
