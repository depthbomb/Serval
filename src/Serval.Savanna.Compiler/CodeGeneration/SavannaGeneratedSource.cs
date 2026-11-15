#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

namespace Serval.Savanna.Compiler.CodeGeneration;

/// <summary>Carries generated C# and mappings back to templates.</summary>
public sealed class SavannaGeneratedSource
{
    /// <summary>The source-generator hint name for this output.</summary>
    public string HintName { get; }

    /// <summary>The complete generated C# text.</summary>
    public string Text { get; }

    /// <summary>The source ranges that connect generated C# to template content.</summary>
    public IReadOnlyList<SavannaSourceMapping> Mappings { get; }

    /// <summary>Creates a generated source entry with its template mappings.</summary>
    /// <param name="hintName">The source-generator hint name.</param>
    /// <param name="text">The complete generated C# text.</param>
    /// <param name="mappings">Mappings from generated spans to template spans.</param>
    public SavannaGeneratedSource(string hintName, string text, IReadOnlyList<SavannaSourceMapping> mappings)
    {
        HintName = hintName;
        Text     = text;
        Mappings = mappings;
    }
}
