#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

namespace Serval.Routing;

// Linked into the generator too, so build-time checks and runtime routes agree on the grammar.
internal sealed class RoutePattern
{
    public string                      Text          { get; }
    public bool                        TrailingSlash { get; }
    public IReadOnlyList<RouteSegment> Segments      { get; }

    public RoutePattern(string template)
    {
        Text          = "/" + template.TrimStart('/');
        TrailingSlash = Text.Length > 1 && Text.EndsWith("/", StringComparison.Ordinal);

        var content      = Text.Substring(1, Text.Length - 1 - (TrailingSlash ? 1 : 0));
        var segments     = content.Length == 0 ? Array.Empty<RouteSegment>() : content.Split('/').Select(ParseSegment).ToArray();
        var names        = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var optionalSeen = false;
        for (var index = 0; index < segments.Length; index++)
        {
            var segment = segments[index];
            if (segment.Name is not null && !names.Add(segment.Name))
            {
                throw new ArgumentException($"Duplicate parameter '{segment.Name}' in '{Text}'.");
            }

            if (segment.CatchAll > 0 && index != segments.Length - 1 || optionalSeen && !segment.Optional)
            {
                throw new ArgumentException($"Optional and catch-all parameters must be at the end of '{Text}'.");
            }

            optionalSeen |= segment.Optional;
        }

        Segments = segments;
    }

    public string Signature(bool caseSensitive, bool strictSlashes)
        => string.Join("/", Segments.Select(segment => segment.Literal is { } literal
               ? caseSensitive ? literal : literal.ToUpperInvariant()
               : $"{{{segment.CatchAll}:{string.Join(":", segment.Constraints)}:{segment.Optional}:{segment.Default}}}"))
           + (strictSlashes && TrailingSlash ? "/" : "");

    private static RouteSegment ParseSegment(string text)
    {
        if (!text.StartsWith("{", StringComparison.Ordinal) || !text.EndsWith("}", StringComparison.Ordinal))
        {
            if (text.Length == 0 || text.IndexOfAny(['{', '}', '?', '#']) >= 0)
            {
                throw new ArgumentException($"Invalid route segment '{text}'. Mixed literal and parameter segments are not supported.");
            }

            return new RouteSegment(Uri.UnescapeDataString(text), null, [], false, null, 0);
        }

        var token    = text.Substring(1, text.Length - 2);
        var catchAll = token.StartsWith("**", StringComparison.Ordinal) ? 2 : token.StartsWith("*", StringComparison.Ordinal) ? 1 : 0;
        token = token.Substring(catchAll);
        var optional = token.EndsWith("?", StringComparison.Ordinal);
        token = optional ? token.Substring(0, token.Length - 1) : token;
        string? defaultValue = null;
        var     depth        = 0;
        for (var index = 0; index < token.Length; index++)
        {
            depth += token[index] == '(' ? 1 : token[index] == ')' ? -1 : 0;
            if (token[index] == '=' && depth == 0)
            {
                defaultValue = token.Substring(index + 1);
                token        = token.Substring(0, index);
                optional     = true;
                break;
            }
        }

        var parts = SplitConstraints(token);
        var name  = parts[0];
        if (name.Length == 0 || !name.All(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_'))
        {
            throw new ArgumentException($"Invalid route parameter '{name}'.");
        }

        return new RouteSegment(null, name, parts.Skip(1).ToArray(), optional || catchAll > 0, defaultValue, catchAll);
    }

    private static List<string> SplitConstraints(string text)
    {
        List<string> parts = [];
        var          depth = 0;
        var          start = 0;
        for (var index = 0; index < text.Length; index++)
        {
            depth += text[index] == '(' ? 1 : text[index] == ')' ? -1 : 0;
            if (depth < 0)
            {
                throw new ArgumentException($"Unbalanced route constraint '{text}'.");
            }

            if (text[index] == ':' && depth == 0)
            {
                parts.Add(text.Substring(start, index - start));
                start = index + 1;
            }
        }

        if (depth != 0)
        {
            throw new ArgumentException($"Unbalanced route constraint '{text}'.");
        }

        parts.Add(text.Substring(start));
        return parts;
    }
}

internal sealed class RouteSegment
{
    public string?  Literal     { get; }
    public string?  Name        { get; }
    public string[] Constraints { get; }
    public bool     Optional    { get; }
    public string?  Default     { get; }
    public int      CatchAll    { get; }

    public RouteSegment(string? literal, string? name, string[] constraints, bool optional, string? defaultValue, int catchAll)
    {
        Literal     = literal;
        Name        = name;
        Constraints = constraints;
        Optional    = optional;
        Default     = defaultValue;
        CatchAll    = catchAll;
    }
}
