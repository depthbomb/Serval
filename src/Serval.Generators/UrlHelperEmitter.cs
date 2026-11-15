#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using System.Text;
using Serval.Routing;

namespace Serval.Generators;

internal static class UrlHelperEmitter
{
    public static string HelperName(string name)
    {
        var result = new StringBuilder();
        var upper  = true;
        foreach (var character in name)
        {
            if (character is not (>= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9'))
            {
                upper = true;
                continue;
            }

            result.Append(upper ? char.ToUpperInvariant(character) : character);
            upper = false;
        }

        if (result.Length == 0 || char.IsDigit(result[0]))
        {
            result.Insert(0, "Route");
        }

        var helper = result.ToString();
        return helper is "Path" or "GetType" or "ToString" or "Equals" or "GetHashCode" or "ReferenceEquals" or "RouteUrlExtensions" ? helper + "Path" : helper;
    }

    public static bool HasUniqueParameters(RoutePattern pattern)
    {
        var names = pattern.Segments.Where(segment => segment.Name is not null).Select(segment => Identifier(segment.Name!)).ToArray();
        return names.Distinct(StringComparer.Ordinal).Count() == names.Length;
    }

    public static void Append(StringBuilder code, string name, string helper, RoutePattern pattern)
    {
        var parameters = pattern.Segments.Where(segment => segment.Name is not null).ToArray();
        var usedNames  = new HashSet<string>(parameters.Select(segment => Identifier(segment.Name!)), StringComparer.Ordinal);
        var receiver   = Unique("__urls", usedNames);
        var query      = Unique("query", usedNames);
        var values     = Unique("__values", usedNames);
        var arguments  = parameters.Select(segment => $"{ParameterType(segment)} @{Identifier(segment.Name!)}{(segment.Optional ? " = null" : "")}").ToList();
        arguments.Add($"global::System.Collections.Generic.IReadOnlyDictionary<string, object?>? @{query} = null");
        code.AppendLine($"    extension(global::Serval.Routing.IUrlGenerator @{receiver})\n    {{");
        code.AppendLine($"        /// <summary>Generates a path for the {Xml(name)} route using the application's routing options.</summary>");
        foreach (var segment in parameters)
        {
            code.AppendLine($"        /// <param name=\"{Identifier(segment.Name!)}\">The {Xml(segment.Name!)} route value{(segment.Optional ? "; null uses the route's default or omits the segment" : "")}.</param>");
        }

        code.AppendLine($"        /// <param name=\"{query}\">Optional query values; null entries are omitted.</param>");
        code.AppendLine("        /// <returns>The escaped application-relative path.</returns>");
        code.AppendLine("        /// <exception cref=\"global::System.ArgumentException\">The route is unregistered or its values violate a constraint.</exception>");
        code.AppendLine($"        public string @{helper}({string.Join(", ", arguments)})\n        {{");
        if (parameters.Length > 0)
        {
            code.AppendLine($"            var @{values} = new global::System.Collections.Generic.Dictionary<string, object?>({parameters.Length});");
            foreach (var segment in parameters)
            {
                var parameter  = "@" + Identifier(segment.Name!);
                var assignment = $"@{values}.Add({ControllerGenerator.Literal(segment.Name!)}, {parameter});";
                if (segment.Optional)
                {
                    code.AppendLine($"            if ({parameter} is not null)\n            {{\n                {assignment}\n            }}");
                }
                else
                {
                    code.AppendLine("            " + assignment);
                }
            }
        }

        code.AppendLine($"            return @{receiver}.Path({ControllerGenerator.Literal(name)}, {(parameters.Length == 0 ? "null" : "@" + values)}, @{query});\n        }}\n    }}");
    }

    private static string ParameterType(RouteSegment segment)
    {
        // Catch-alls represent paths even if a constraint restricts their contents.
        var types = segment.CatchAll == 0
            ? segment.Constraints.Select(constraint => constraint.ToLowerInvariant() switch
            {
                "int"      => "int",
                "long"     => "long",
                "guid"     => "global::System.Guid",
                "bool"     => "bool",
                "datetime" => "global::System.DateTimeOffset",
                "decimal"  => "decimal",
                "double"   => "double",
                "float"    => "float",
                _          => null,
            }).Where(type => type is not null).Distinct().ToArray()
            : [];
        // Conflicting scalar constraints have no single safe inferred type. Keep their textual contract.
        var type = types.Length == 1 ? types[0]! : "string";
        return type + (segment.Optional ? "?" : "");
    }

    private static string Identifier(string name) => char.IsDigit(name[0]) ? "_" + name : name;

    private static string Unique(string name, HashSet<string> used)
    {
        while (!used.Add(name))
        {
            name = "_" + name;
        }

        return name;
    }

    private static string Xml(string value)
        => value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;").Replace("\r", "&#13;").Replace("\n", "&#10;");
}
