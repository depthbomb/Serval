#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Serval.Savanna.Compiler.Syntax;

namespace Serval.Savanna.Compiler.Html;

internal sealed class SavannaHtmlMinifier
{
    private bool _afterBlock;
    private bool _requiresOriginal;
    private bool IsPreserved => _elements.Count > 0 && _elements[_elements.Count - 1].Preserve;

    private readonly Stack<Branch>              _branches = new();
    private readonly List<Element>              _elements = [];
    private readonly IReadOnlyList<SavannaNode> _expressions;
    private readonly string                     _marker;
    private readonly HashSet<SavannaNode>       _preserved = [];
    private readonly SavannaRawText             _rawText   = new();

    private static readonly Regex           PreserveAttribute     = new(@"\sdata-savanna-preserve-whitespace(?=[\s=/>])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly HashSet<string> VoidElements          = [with(StringComparer.OrdinalIgnoreCase), "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "param", "source", "track", "wbr"];
    private static readonly HashSet<string> RawElements           = [with(StringComparer.OrdinalIgnoreCase), "script", "style", "textarea", "title", "xmp", "iframe", "noembed", "noframes", "plaintext"];
    private static readonly HashSet<string> BlockElements         = [with(StringComparer.OrdinalIgnoreCase), "html", "head", "body", "address", "article", "aside", "blockquote", "div", "dl", "dt", "dd", "fieldset", "figcaption", "figure", "footer", "form", "h1", "h2", "h3", "h4", "h5", "h6", "header", "hgroup", "hr", "li", "main", "menu", "nav", "ol", "p", "pre", "section", "table", "thead", "tbody", "tfoot", "tr", "td", "th", "caption", "ul"];
    private static readonly HashSet<string> ElementOnlyContainers = [with(StringComparer.OrdinalIgnoreCase), "html", "head", "ul", "ol", "menu", "dl", "table", "thead", "tbody", "tfoot", "tr", "colgroup"];

    private SavannaHtmlMinifier(IReadOnlyList<SavannaNode> expressions, string marker)
    {
        _expressions = expressions;
        _marker      = marker;
    }

    public static (IReadOnlyDictionary<SavannaNode, string> Text, ISet<SavannaNode> Preserved) Minify(IReadOnlyList<SavannaNode> nodes)
    {
        var marker = "\uE000savanna";
        // Placeholders let HTML span several text nodes without losing the expressions between them.
        // Pick a marker absent from the input so literal template text can't be mistaken for a placeholder.
        while (nodes.Any(node => node.Value.Contains(marker)))
        {
            marker += "_";
        }

        var          input       = new StringBuilder();
        var          expressions = new List<SavannaNode>();
        var          groups      = new List<SavannaNode?>();
        SavannaNode? group       = null;
        foreach (var node in nodes)
        {
            if (node.Kind == SavannaNodeKind.Text)
            {
                group ??= node;
                input.Append(node.Value);
            }
            else
            {
                groups.Add(group);
                group = null;
                input.Append(marker).Append(expressions.Count).Append('\uE001');
                expressions.Add(node);
            }
        }

        groups.Add(group);
        var minifier = new SavannaHtmlMinifier(expressions, marker);
        var compact  = minifier.Compact(input.ToString());
        if (minifier._requiresOriginal)
        {
            // Dynamic markup and branch-dependent tag boundaries can change whitespace semantics.
            compact = input.ToString();
            minifier._preserved.UnionWith(expressions);
        }

        var text  = new Dictionary<SavannaNode, string>();
        var start = 0;
        for (var index = 0; index <= expressions.Count; index++)
        {
            var separator = marker + index + '\uE001';
            var end       = index == expressions.Count ? compact.Length : compact.IndexOf(separator, start, StringComparison.Ordinal);
            if (groups[index] is {} first)
            {
                text[first] = compact.Substring(start, end - start);
            }

            start = end + separator.Length;
        }

        return (text, minifier._preserved);
    }

    private string Compact(string input)
    {
        var output = new StringBuilder(input.Length);
        var index  = 0;
        while (index < input.Length)
        {
            if (index + _marker.Length <= input.Length && string.Compare(input, index, _marker, 0, _marker.Length, StringComparison.Ordinal) == 0)
            {
                var end    = input.IndexOf('\uE001', index);
                var number = int.Parse(input.Substring(index + _marker.Length, end - index - _marker.Length), CultureInfo.InvariantCulture);
                Visit(_expressions[number]);
                output.Append(input, index, end - index + 1);
                index = end + 1;
                continue;
            }

            var raw = _elements.Count > 0 && RawElements.Contains(_elements[_elements.Count - 1].Name)
                ? _elements[_elements.Count - 1].Name
                : null;
            if (raw is not null && !_rawText.IsClosingTag(input, index, raw))
            {
                output.Append(input[index++]);
                continue;
            }

            if (StartsWith(input, index, "<!--"))
            {
                var end = input.IndexOf("-->", index + 4, StringComparison.Ordinal);
                end = end < 0 ? input.Length : end + 3;
                var comment = input.Substring(index, end - index);
                output.Append(IsPreserved ? comment : CollapseWhitespace(comment));
                index = end;
                continue;
            }

            if (input[index] == '<' && index + 1 < input.Length && (char.IsLetter(input[index + 1]) || input[index + 1] is '/' or '!' or '?'))
            {
                var end = TagEnd(input, index);
                if (end < 0)
                {
                    output.Append(input, index, input.Length - index);
                    break;
                }

                var tag       = input.Substring(index, end - index + 1);
                var closing   = tag.Length > 1 && tag[1] == '/';
                var nameStart = closing ? 2 : 1;
                var nameEnd   = nameStart;
                while (nameEnd < tag.Length && (char.IsLetterOrDigit(tag[nameEnd]) || tag[nameEnd] is ':' or '-'))
                {
                    nameEnd++;
                }

                var name     = tag.Substring(nameStart, nameEnd - nameStart).ToLowerInvariant();
                var preserve = IsPreserved || name is "pre" or "svg" or "math" || RawElements.Contains(name) || PreserveAttribute.IsMatch(tag);
                output.Append(preserve ? tag : CompactTag(tag));
                _afterBlock = BlockElements.Contains(name) || StartsWith(tag, 0, "<!doctype");
                if (closing)
                {
                    var match = _elements.FindLastIndex(element => element.Name == name);
                    if (match >= 0)
                    {
                        _elements.RemoveRange(match, _elements.Count - match);
                    }
                }
                else if (name.Length > 0 && !VoidElements.Contains(name)
                                         && !(tag.EndsWith("/>", StringComparison.Ordinal) && (name is "svg" or "math" || _elements.Any(element => element.Name is "svg" or "math"))))
                {
                    _elements.Add(new Element(name, preserve));
                    _rawText.Reset();
                }

                index = end + 1;
                continue;
            }

            if (!IsPreserved && IsWhitespace(input[index]))
            {
                while (index < input.Length && IsWhitespace(input[index]))
                {
                    index++;
                }

                var elementOnly = _elements.Count > 0 && ElementOnlyContainers.Contains(_elements[_elements.Count - 1].Name);
                if (!_afterBlock && !elementOnly && !IsBlockTag(input, index))
                {
                    output.Append(' ');
                }
            }
            else
            {
                _afterBlock = false;
                output.Append(input[index++]);
            }
        }

        return output.ToString();
    }

    private void Visit(SavannaNode node)
    {
        if (IsPreserved)
        {
            _preserved.Add(node);
        }

        // Every branch must leave the same element stack before whitespace can be collapsed safely.
        var state = string.Join("/", _elements.Select(element => element.Name + (element.Preserve ? "!" : "")));
        switch (node.Kind)
        {
            case SavannaNodeKind.Let:
            case SavannaNodeKind.Whitespace:
            case SavannaNodeKind.EndWhitespace:
            case SavannaNodeKind.Break:
            case SavannaNodeKind.Continue:
                break;
            case SavannaNodeKind.Html:
                _requiresOriginal = true;
                _afterBlock       = false;
                break;
            case SavannaNodeKind.If:
            case SavannaNodeKind.Unless:
            case SavannaNodeKind.Debug:
            case SavannaNodeKind.Release:
            case SavannaNodeKind.Switch:
            case SavannaNodeKind.For:
            case SavannaNodeKind.ForEach:
            case SavannaNodeKind.AwaitForEach:
                _branches.Push(new Branch(state, _afterBlock));
                break;
            case SavannaNodeKind.Else:
            case SavannaNodeKind.ElseIf:
            case SavannaNodeKind.Empty:
            case SavannaNodeKind.Case:
            case SavannaNodeKind.Default:
                if (_branches.Count > 0)
                {
                    var branch = _branches.Peek();
                    _requiresOriginal  |= branch.State != state;
                    branch.EndsAtBlock &= _afterBlock;
                    branch.HasElse     |= node.Kind is SavannaNodeKind.Else or SavannaNodeKind.Empty or SavannaNodeKind.Default;
                    _afterBlock        =  branch.BeforeBlock;
                }

                break;
            case SavannaNodeKind.EndIf:
            case SavannaNodeKind.EndBuild:
            case SavannaNodeKind.EndSwitch:
            case SavannaNodeKind.EndFor:
            case SavannaNodeKind.EndForEach:
                if (_branches.Count > 0)
                {
                    var branch = _branches.Pop();
                    _requiresOriginal |= branch.State != state;
                    _afterBlock       &= branch.EndsAtBlock && (branch.HasElse || branch.BeforeBlock);
                }

                break;
            default:
                _afterBlock = false;
                break;
        }
    }

    private string CompactTag(string tag)
    {
        var output = new StringBuilder(tag.Length);
        var index  = 0;
        while (index < tag.Length)
        {
            var character = tag[index];
            if (character is '\'' or '"')
            {
                var end = tag.IndexOf(character, index + 1);
                if (end < 0)
                {
                    output.Append(tag, index, tag.Length - index);
                    break;
                }

                var value = tag.Substring(index + 1, end - index - 1)
                               .Replace("\r\n", "\n")
                               .Replace('\r', '\n')
                               .Replace("\n", "&#10;")
                               .Replace("\t", "&#9;")
                               .Replace("\f", "&#12;");
                // Dynamic values still need quotes: their eventual characters aren't known at compile time.
                var unquote = output.Length > 0 && output[output.Length - 1] == '=' && value.Length > 0 && !value.Contains(_marker) && !value.Any(valueCharacter => IsWhitespace(valueCharacter) || valueCharacter is '"' or '\'' or '`' or '=' or '<' or '>');
                if (unquote)
                {
                    output.Append(value);
                    if (end + 1 < tag.Length && tag[end + 1] == '/')
                    {
                        output.Append(' ');
                    }
                }
                else
                {
                    output.Append(character).Append(value).Append(character);
                }

                index = end + 1;
            }
            else if (IsWhitespace(character))
            {
                while (index < tag.Length && IsWhitespace(tag[index]))
                {
                    index++;
                }

                var previous = output.Length > 0 ? output[output.Length - 1] : '\0';
                if (index < tag.Length && tag[index] != '=' && tag[index] != '>' && previous != '=')
                {
                    output.Append(' ');
                }
            }
            else
            {
                output.Append(character);
                index++;
            }
        }

        return output.ToString();
    }

    private static bool IsBlockTag(string text, int start)
    {
        if (start >= text.Length || text[start] != '<')
        {
            return false;
        }

        if (StartsWith(text, start, "<!doctype"))
        {
            return true;
        }

        var nameStart = start + 1;
        if (nameStart < text.Length && text[nameStart] == '/')
        {
            nameStart++;
        }

        var end = nameStart;
        while (end < text.Length && char.IsLetterOrDigit(text[end]))
        {
            end++;
        }

        return end > nameStart && end < text.Length && (IsWhitespace(text[end]) || text[end] is '/' or '>')
               && BlockElements.Contains(text.Substring(nameStart, end - nameStart));
    }

    private static int TagEnd(string text, int start)
    {
        var quote = '\0';
        for (var index = start + 1; index < text.Length; index++)
        {
            var character = text[index];
            if (quote != '\0')
            {
                if (character == quote)
                {
                    quote = '\0';
                }
            }
            else if (character is '\'' or '"')
            {
                quote = character;
            }
            else if (character == '>')
            {
                return index;
            }
        }

        return -1;
    }

    private static bool StartsWith(string text, int index, string value)
        => index + value.Length <= text.Length && string.Compare(text, index, value, 0, value.Length, StringComparison.OrdinalIgnoreCase) == 0;

    private static bool IsWhitespace(char character) => character is ' ' or '\t' or '\r' or '\n' or '\f';

    private static string CollapseWhitespace(string text)
    {
        var output     = new StringBuilder(text.Length);
        var whitespace = false;
        foreach (var character in text)
        {
            if (IsWhitespace(character))
            {
                if (!whitespace)
                {
                    output.Append(' ');
                }

                whitespace = true;
            }
            else
            {
                output.Append(character);
                whitespace = false;
            }
        }

        return output.ToString();
    }

    private sealed class Element
    {
        public string Name     { get; }
        public bool   Preserve { get; }

        public Element(string name, bool preserve)
        {
            Name     = name;
            Preserve = preserve;
        }
    }

    private sealed class Branch
    {
        public string State       { get; }
        public bool   BeforeBlock { get; }
        public bool   EndsAtBlock { get; set; }
        public bool   HasElse     { get; set; }

        public Branch(string state, bool beforeBlock)
        {
            State       = state;
            BeforeBlock = beforeBlock;
            EndsAtBlock = true;
        }
    }
}
