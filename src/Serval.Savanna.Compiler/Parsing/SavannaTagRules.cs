#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

namespace Serval.Savanna.Compiler.Parsing;

/// <summary>Shares the directive vocabulary between parsing and editor tooling.</summary>
public static class SavannaTagRules
{
    // The parser and editor use the same attribute vocabulary.
    /// <summary>Maps case-sensitive directive names to their ordered attribute names.</summary>
    /// <remarks>
    ///     The first attribute is the primary one when present. Treat the returned arrays as shared read-only metadata.
    /// </remarks>
    public static IReadOnlyDictionary<string, string[]> Attributes { get; } = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["model"]      = ["type"],
        ["using"]      = ["namespace"],
        ["layout"]     = [],
        ["extends"]    = ["view", "model"],
        ["import"]     = ["view", "as"],
        ["block"]      = ["name", "required"],
        ["parent"]     = [],
        ["if"]         = ["condition"],
        ["elseif"]     = ["condition"],
        ["else"]       = [],
        ["unless"]     = ["condition"],
        ["debug"]      = [],
        ["release"]    = [],
        ["switch"]     = ["value"],
        ["case"]       = ["value"],
        ["default"]    = [],
        ["break"]      = ["if"],
        ["continue"]   = ["if"],
        ["foreach"]    = ["item", "in", "await"],
        ["for"]        = ["clause"],
        ["empty"]      = [],
        ["include"]    = ["view", "model"],
        ["component"]  = ["type", "arguments"],
        ["let"]        = ["name", "value"],
        ["html"]       = ["value"],
        ["json"]       = ["id", "value"],
        ["slot"]       = ["name", "required"],
        ["verbatim"]   = [],
        ["whitespace"] = ["mode"],
    };

    /// <summary>Returns the primary attribute for a directive.</summary>
    /// <param name="name">The case-sensitive directive name.</param>
    /// <returns>The first attribute name, or null for unknown directives or directives without attributes.</returns>
    public static string? Primary(string name) => Attributes.TryGetValue(name, out var values) && values.Length > 0 ? values[0] : null;

    /// <summary>Checks whether a directive is defined without a paired body.</summary>
    /// <param name="name">The case-sensitive directive name.</param>
    /// <returns>
    ///     True for standalone declarations and operations; false for paired, branch, or unknown directives.
    /// </returns>
    public static bool IsStandalone(string name) => name is "model" or "using" or "layout" or "extends" or "import" or "parent" or "let" or "html" or "json" or "break" or "continue";

    internal static void Validate(SavannaTag tag, string path, List<SavannaDiagnostic> diagnostics)
    {
        if (!Attributes.TryGetValue(tag.Name, out var allowed))
        {
            Error("Unknown Savanna tag: " + tag.Name);
            return;
        }

        var paired       = tag.Name is "if" or "unless" or "debug" or "release" or "switch" or "foreach" or "for" or "block" or "whitespace" or "verbatim";
        var optionalPair = tag.Name is "include" or "component" or "slot";
        var branch       = tag.Name is "else" or "elseif" or "empty" or "case" or "default";
        if (tag.Closing)
        {
            if (!paired && !optionalPair || tag.Attributes.Count > 0 || tag.SelfClosing)
            {
                Error("Invalid closing Savanna tag.");
            }

            return;
        }

        if ((paired || branch) && tag.SelfClosing)
        {
            Error("This Savanna tag cannot be self-closing.");
        }

        foreach (var attribute in tag.Attributes.Values)
        {
            if (attribute.Name is not ("required" or "await") && attribute.Span.Length == 0)
            {
                Error("The '" + attribute.Name + "' attribute requires a quoted value.");
            }

            if (!allowed.Contains(attribute.Name))
            {
                Error("Unknown attribute '" + attribute.Name + "' on " + tag.Name + ".");
            }

            if (attribute.Name is "required" or "await" && attribute.Value != "true")
            {
                Error("Use the flag attribute '" + attribute.Name + "' without a value.");
            }
        }

        foreach (var name in allowed.Where(name => name is not ("required" or "await" or "model" or "if")))
        {
            if (tag.Get(name) is not { Value.Length: > 0 })
            {
                Error("The " + tag.Name + " tag requires '" + name + "'.");
            }
        }

        return;

        void Error(string message)
        {
            diagnostics.Add(new SavannaDiagnostic("SAVANNA001", message, path, tag.Span));
        }
    }
}
