#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using System.Text.RegularExpressions;

namespace Serval.Routing;

internal sealed class RouteConstraint
{
    public           string             Signature { get; }
    private readonly Func<string, bool> _matches;

    public RouteConstraint(string expression, ServalOptions options)
    {
        Signature = expression;

        _matches = RouteConstraintParser.Parse(expression, options.CaseSensitivePaths, (name, arguments) => options.RouteConstraints.TryGetValue(name, out var custom) ? value => custom(value, arguments) : null);
    }

    public bool Matches(string value)
    {
        try
        {
            return _matches(value);
        }
        catch (RegexMatchTimeoutException)
        {
            // A costly user-supplied pattern fails this candidate without turning routing into a 500.
            return false;
        }
    }
}
