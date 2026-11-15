#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.CodeAnalysis.Text;

namespace Serval.Savanna.Compiler.Parsing;

/// <summary>Stores a Savanna directive attribute without HTML-decoding its C# content.</summary>
public sealed class SavannaAttribute
{
    /// <summary>The case-sensitive directive attribute name.</summary>
    public string Name { get; }

    /// <summary>The raw attribute content, or true for a valueless flag.</summary>
    public string Value { get; }

    /// <summary>The UTF-16 source range of the unquoted value; flag values have an empty range.</summary>
    public TextSpan Span { get; }

    /// <summary>The attribute name's UTF-16 source range.</summary>
    public TextSpan NameSpan { get; internal set; }

    /// <summary>Creates an attribute with its value and source location.</summary>
    /// <param name="name">The case-sensitive attribute name.</param>
    /// <param name="value">The raw value without surrounding quotes, or true for a flag.</param>
    /// <param name="span">The source range of the unquoted value.</param>
    public SavannaAttribute(string name, string value, TextSpan span)
    {
        Name  = name;
        Value = value;
        Span  = span;
    }
}

// Attribute contents are C# source, not HTML. Use the other quote delimiter when
// an expression contains a string. Keeping source verbatim also keeps IDE ranges exact.
/// <summary>Represents a tag-style Savanna directive and its raw C# attributes.</summary>
/// <remarks>
///     Attribute contents are C# source rather than HTML. Use the other quote delimiter when a C# expression contains a
///     string literal.
/// </remarks>
public sealed class SavannaTag
{
    /// <summary>The directive name, without Savanna delimiters.</summary>
    public string Name { get; private set; } = "";

    /// <summary>Whether the tag uses a closing directive delimiter.</summary>
    public bool Closing { get; private set; }

    /// <summary>Whether the tag ends with a slash before its closing angle bracket.</summary>
    public bool SelfClosing { get; private set; }

    /// <summary>The UTF-16 range covering the complete tag, including delimiters.</summary>
    public TextSpan Span { get; private set; }

    /// <summary>The case-sensitive attributes parsed from the directive.</summary>
    public IReadOnlyDictionary<string, SavannaAttribute> Attributes => _attributes;

    private readonly Dictionary<string, SavannaAttribute> _attributes = new(StringComparer.Ordinal);

    /// <summary>Looks up an attribute by its exact name.</summary>
    /// <param name="name">The case-sensitive attribute name.</param>
    /// <returns>The attribute, or null when it is absent.</returns>
    public SavannaAttribute? Get(string name) => _attributes.TryGetValue(name, out var value) ? value : null;

    /// <summary>Reads a tag-style directive and appends malformed-syntax diagnostics.</summary>
    /// <param name="text">The full source text.</param>
    /// <param name="start">The UTF-16 offset of an opening or closing Savanna tag delimiter.</param>
    /// <param name="path">The path used for diagnostics.</param>
    /// <param name="diagnostics">The caller-owned list receiving any errors.</param>
    /// <returns>The parsed tag, including recoverable attributes from incomplete input.</returns>
    /// <remarks>
    ///     The caller must position start at a Savanna tag delimiter with enough characters to read its opening. This method
    ///     does not validate the directive's allowed attributes; parsing does that separately.
    /// </remarks>
    public static SavannaTag Read(string text, int start, string path, List<SavannaDiagnostic> diagnostics)
    {
        var tag       = new SavannaTag { Closing = text[start + 1] == '/' };
        var offset    = start + (tag.Closing ? 3 : 2);
        var nameStart = offset;
        while (offset < text.Length && IsName(text[offset]))
        {
            offset++;
        }

        tag.Name = text.Substring(nameStart, offset - nameStart);
        while (offset < text.Length)
        {
            while (offset < text.Length && char.IsWhiteSpace(text[offset]))
            {
                offset++;
            }

            if (offset >= text.Length)
            {
                break;
            }

            if (text[offset] == '>')
            {
                offset++;
                tag.Span = TextSpan.FromBounds(start, offset);
                return tag;
            }

            if (text[offset] == '/' && offset + 1 < text.Length && text[offset + 1] == '>')
            {
                tag.SelfClosing =  true;
                offset          += 2;
                tag.Span        =  TextSpan.FromBounds(start, offset);
                return tag;
            }

            var attributeStart = offset;
            while (offset < text.Length && IsName(text[offset]))
            {
                offset++;
            }

            if (offset == attributeStart)
            {
                Error("Expected a named attribute or the end of the Savanna tag.", offset++);
                continue;
            }

            var name = text.Substring(attributeStart, offset - attributeStart);
            while (offset < text.Length && char.IsWhiteSpace(text[offset]))
            {
                offset++;
            }

            var valueStart = offset;
            var valueEnd   = offset;
            var value      = "true";
            if (offset < text.Length && text[offset] == '=')
            {
                offset++;
                while (offset < text.Length && char.IsWhiteSpace(text[offset]))
                {
                    offset++;
                }

                if (offset >= text.Length || text[offset] is not ('\'' or '"'))
                {
                    Error("Savanna attribute values must be quoted.", Math.Min(offset, text.Length));
                    continue;
                }

                var quote = text[offset++];
                valueStart = offset;
                while (offset < text.Length && text[offset] != quote)
                {
                    offset++;
                }

                valueEnd = offset;
                value    = text.Substring(valueStart, valueEnd - valueStart);
                if (offset < text.Length)
                {
                    offset++;
                }
            }

            if (tag._attributes.ContainsKey(name))
            {
                Error("Duplicate Savanna attribute: " + name, attributeStart);
            }
            else
            {
                tag._attributes.Add(name, new SavannaAttribute(name, value, TextSpan.FromBounds(valueStart, valueEnd)) { NameSpan = new TextSpan(attributeStart, name.Length) });
            }
        }

        tag.Span = TextSpan.FromBounds(start, offset);
        Error("Unterminated Savanna tag.", start);
        return tag;

        void Error(string message, int position)
        {
            diagnostics.Add(new SavannaDiagnostic("SAVANNA001", message, path, new TextSpan(position, 0)));
        }
    }

    private static bool IsName(char value) => char.IsLetterOrDigit(value) || value is '_' or '-';
}
