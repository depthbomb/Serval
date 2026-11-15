#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using System.Globalization;
using System.Text;

namespace Serval.Routing;

internal sealed class RouteTemplate
{
    public           string        Text          { get; }
    public           string        Signature     { get; }
    public           string        Precedence    { get; }
    public           string[]      LiteralPrefix { get; }
    private readonly int           _maximumSegments;
    private readonly int           _minimumSegments;
    private readonly ServalOptions _options;
    private readonly int           _parameterCount;
    private readonly Segment[]     _segments;
    private readonly bool          _trailingSlash;

    public RouteTemplate(string template, ServalOptions options)
    {
        var pattern = new RoutePattern(template);
        _options         = options;
        Text             = pattern.Text;
        _trailingSlash   = pattern.TrailingSlash;
        _segments        = pattern.Segments.Select(segment => BindSegment(segment, options)).ToArray();
        _minimumSegments = _segments.Count(segment => !segment.Optional);
        _maximumSegments = _segments.LastOrDefault()?.CatchAll > 0 ? int.MaxValue : _segments.Length;
        _parameterCount  = _segments.Count(segment => segment.Name is not null);

        LiteralPrefix = _segments.TakeWhile(segment => segment.Literal is not null).Select(segment => segment.Literal!).ToArray();
        // Lexical ordering gives literals first, then constrained parameters, plain parameters, and catch-alls.
        // Parameter names stay out of the signature: renaming a capture doesn't make a route distinct.
        Precedence    = string.Concat(_segments.Select(segment => segment.Literal is not null ? '4' : segment.CatchAll > 0 ? '1' : segment.Constraints.Length > 0 ? '3' : '2'));
        Signature     = pattern.Signature(options.CaseSensitivePaths, options.TrailingSlashes == TrailingSlashBehavior.Strict);
    }

    public bool TryMatch(string[] path, bool hasTrailingSlash, out Dictionary<string, string> values)
    {
        values = null!;
        if (path.Length < _minimumSegments || path.Length > _maximumSegments || _options.TrailingSlashes == TrailingSlashBehavior.Strict && _trailingSlash != hasTrailingSlash)
        {
            return false;
        }

        var     cursor        = 0;
        string? catchAllValue = null;
        foreach (var segment in _segments)
        {
            var value = segment.CatchAll > 0 ? string.Join('/', path, cursor, path.Length - cursor) : cursor < path.Length ? path[cursor] : segment.Default;
            if (segment.CatchAll > 0)
            {
                catchAllValue = value;
            }

            if (value is null)
            {
                if (segment.Optional)
                {
                    continue;
                }

                return false;
            }

            if (value.Length == 0 && segment.CatchAll == 0 && cursor < path.Length)
            {
                return false;
            }

            if (segment.Literal is { } literal)
            {
                if (!literal.Equals(value, _options.CaseSensitivePaths ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
            else
            {
                foreach (var constraint in segment.Constraints)
                {
                    if (!constraint.Matches(value))
                    {
                        return false;
                    }
                }
            }

            cursor = segment.CatchAll > 0 ? path.Length : Math.Min(cursor + 1, path.Length);
        }

        if (cursor != path.Length)
        {
            return false;
        }

        // Most candidates fail a later literal or constraint. Capture values only
        // after the entire route matches, without evaluating constraints twice.
        values = new Dictionary<string, string>(_parameterCount, StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < _segments.Length; index++)
        {
            var segment = _segments[index];
            if (segment.Name is null)
            {
                continue;
            }

            var value = segment.CatchAll > 0 ? catchAllValue : index < path.Length ? path[index] : segment.Default;
            if (value is not null)
            {
                values.Add(segment.Name, value);
            }
        }

        return true;
    }

    public string Generate(IReadOnlyDictionary<string, object?> values)
    {
        var result  = new StringBuilder();
        var omitted = false;
        foreach (var segment in _segments)
        {
            if (segment.Literal is { } literal)
            {
                result.Append('/').Append(Uri.EscapeDataString(literal));
                continue;
            }

            var value = values.TryGetValue(segment.Name!, out var supplied) ? Convert.ToString(supplied, CultureInfo.InvariantCulture) : segment.Default;
            if (string.IsNullOrEmpty(value))
            {
                if (segment.Optional || segment.CatchAll > 0)
                {
                    omitted = true;
                    continue;
                }

                throw new ArgumentException($"Route value '{segment.Name}' is required for '{Text}'.");
            }

            // Once an optional segment is omitted, a later value would slide into the wrong parameter.
            if (omitted || !segment.Constraints.All(constraint => constraint.Matches(value)))
            {
                throw new ArgumentException($"Route value '{segment.Name}' does not satisfy '{Text}'.");
            }

            // ** preserves path separators; * treats the captured value as one escaped segment.
            result.Append('/').Append(segment.CatchAll == 2 ? string.Join('/', value.Split('/').Select(Uri.EscapeDataString)) : Uri.EscapeDataString(value));
        }

        var url = result.Length == 0 ? "/" : result.ToString();
        if (url != "/" && (_options.TrailingSlashes == TrailingSlashBehavior.RedirectToWithSlash || _options.TrailingSlashes == TrailingSlashBehavior.Strict && _trailingSlash))
        {
            url += "/";
        }

        return url;
    }

    private static Segment BindSegment(RouteSegment segment, ServalOptions options)
    {
        var constraints = segment.Constraints.Select(value => new RouteConstraint(value, options)).ToArray();
        if (segment.Default is not null && !constraints.All(constraint => constraint.Matches(segment.Default)))
        {
            throw new ArgumentException($"Default value for '{segment.Name}' does not satisfy its constraints.");
        }

        return new Segment(segment.Literal, segment.Name, constraints, segment.Optional, segment.Default, segment.CatchAll);
    }

    private sealed record Segment(string? Literal, string? Name, RouteConstraint[] Constraints, bool Optional, string? Default, int CatchAll);
}
