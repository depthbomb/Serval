#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using System.Globalization;
using System.Text.RegularExpressions;

namespace Serval.Routing;

internal static class RouteConstraintParser
{
    public static Func<string, bool> Parse(string expression, bool caseSensitive, Func<string, string[], Func<string, bool>?> custom)
    {
        // This parser also builds into the .NET Standard 2.0 generator, which has no Range/Index types.
        var start      = expression.IndexOf('(');
        var name       = start < 0 ? expression : expression.Substring(0, start);
        var arguments  = start < 0 ? "" : expression.EndsWith(")", StringComparison.Ordinal) ? expression.Substring(start + 1, expression.Length - start - 2) : throw new ArgumentException($"Invalid constraint '{expression}'.");
        var parts      = start < 0 ? Array.Empty<string>() : arguments.Split(',').Select(part => part.Trim()).ToArray();
        var normalized = name.ToLowerInvariant();
        var count = normalized switch
        {
            "int" or "long" or "guid" or "bool" or "datetime" or "decimal" or "double" or "float" or "alpha" => 0,
            "min" or "max" or "minlength" or "maxlength"                                                     => 1,
            "range"                                                                                          => 2,
            "length"                                                                                         => parts.Length == 1 ? 1 : 2,
            _                                                                                                => -1,
        };
        if (name.Length == 0 ||
            count >= 0 &&
            (parts.Length != count || count == 0 && start >= 0) ||
            normalized == "regex" &&
            start      < 0)
        {
            throw new ArgumentException($"Invalid arguments for route constraint '{expression}'.");
        }

        return normalized switch
        {
            "int"       => value => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _),
            "long"      => value => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _),
            "guid"      => value => Guid.TryParse(value, out _),
            "bool"      => value => bool.TryParse(value, out _),
            "datetime"  => value => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
            "decimal"   => value => decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out _),
            "double"    => value => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && !double.IsInfinity(number) && !double.IsNaN(number),
            "float"     => value => float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)  && !float.IsInfinity(number)  && !float.IsNaN(number),
            "alpha"     => value => value.Length > 0                                                                         && value.All(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z'),
            "min"       => NumericRange(Number(parts[0]), decimal.MaxValue),
            "max"       => NumericRange(decimal.MinValue, Number(parts[0])),
            "range"     => NumericRange(Number(parts[0]), Number(parts[1])),
            "minlength" => LengthRange(Length(parts[0]), int.MaxValue),
            "maxlength" => LengthRange(0, Length(parts[0])),
            "length"    => LengthRange(Length(parts[0]), Length(parts[parts.Length - 1])),
            "regex"     => new Regex(arguments, RegexOptions.CultureInvariant | (caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase), TimeSpan.FromMilliseconds(100)).IsMatch,
            _           => custom(name, parts) ?? throw new ArgumentException($"Unknown route constraint '{name}'."),
        };
    }

    private static decimal Number(string text)
        => decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new ArgumentException($"Invalid numeric route constraint argument '{text}'.");

    private static int Length(string text)
        => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new ArgumentException($"Invalid length constraint argument '{text}'.");

    private static Func<string, bool> NumericRange(decimal minimum, decimal maximum)
    {
        if (minimum > maximum)
        {
            throw new ArgumentException("A route constraint's minimum cannot exceed its maximum.");
        }

        return value => decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var number) && number >= minimum && number <= maximum;
    }

    private static Func<string, bool> LengthRange(int minimum, int maximum)
    {
        if (minimum > maximum)
        {
            throw new ArgumentException("A length constraint's minimum cannot exceed its maximum.");
        }

        return value => value.Length >= minimum && value.Length <= maximum;
    }
}
