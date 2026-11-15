#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.CodeAnalysis.Text;

namespace Serval.Savanna.Compiler.Syntax;

/// <summary>Holds parsed template declarations, body nodes, and diagnostics.</summary>
/// <remarks>Public consumers can inspect the syntax; parser and compiler stages populate internal setters.</remarks>
public sealed class SavannaDocument
{
    /// <summary>The source path used for diagnostics and mappings.</summary>
    public string Path { get; }

    /// <summary>The exact path relative to the Savanna root, using forward slashes.</summary>
    public string Name { get; }

    /// <summary>The original source text used for all source ranges.</summary>
    public SourceText Text { get; }

    /// <summary>The declared C# model type, defaulting to object.</summary>
    public string Model { get; internal set; } = "object";

    /// <summary>The model type's source range, or the default span when undeclared.</summary>
    public TextSpan ModelSpan { get; internal set; }

    /// <summary>Whether the document is declared as a layout.</summary>
    public bool IsLayout { get; internal set; }

    /// <summary>Whether the relative name is _Imports.svn or ends in /_Imports.svn.</summary>
    public bool IsImports => Name == "_Imports.svn" || Name.EndsWith("/_Imports.svn", StringComparison.Ordinal);

    /// <summary>The extends declaration, or null when no parent layout is selected.</summary>
    public SavannaNode? Parent { get; internal set; }

    /// <summary>Local namespace imports, or merged imports after SavannaImports.Apply.</summary>
    public IReadOnlyList<string> Usings { get; internal set; } = Array.Empty<string>();

    /// <summary>Case-sensitive view aliases mapped to exact relative template paths.</summary>
    public IReadOnlyDictionary<string, string> Imports { get; internal set; } = new Dictionary<string, string>();

    /// <summary>The top-level body nodes in source order.</summary>
    public IReadOnlyList<SavannaNode> Body { get; internal set; } = Array.Empty<SavannaNode>();

    /// <summary>Named block declarations and overrides.</summary>
    public IReadOnlyList<SavannaBlock> Blocks { get; internal set; } = Array.Empty<SavannaBlock>();

    /// <summary>Syntax diagnostics collected while parsing the document.</summary>
    public IReadOnlyList<SavannaDiagnostic> Diagnostics { get; internal set; } = Array.Empty<SavannaDiagnostic>();

    /// <summary>Creates an unparsed document container with an object model default.</summary>
    /// <param name="path">The diagnostic source path.</param>
    /// <param name="name">The path relative to the Savanna root.</param>
    /// <param name="text">The original template text.</param>
    /// <remarks>Use SavannaParser.Parse to populate declarations, body nodes, and diagnostics.</remarks>
    public SavannaDocument(string path, string name, SourceText text)
    {
        Path = path;
        Name = name;
        Text = text;
    }
}
