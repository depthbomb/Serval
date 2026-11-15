#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.CodeAnalysis.Text;

namespace Serval.Savanna.Compiler.CodeGeneration;

/// <summary>Connects a range of generated C# to its original template text.</summary>
public sealed class SavannaSourceMapping
{
    /// <summary>The original template path.</summary>
    public string SourcePath { get; }

    /// <summary>The original template's UTF-16 range.</summary>
    public TextSpan SourceSpan { get; }

    /// <summary>The corresponding generated C# UTF-16 range.</summary>
    public TextSpan GeneratedSpan { get; }

    /// <summary>Creates a mapping between template and generated source ranges.</summary>
    /// <param name="sourcePath">The template source path.</param>
    /// <param name="sourceSpan">The original UTF-16 source range.</param>
    /// <param name="generatedSpan">The corresponding UTF-16 range in generated C#.</param>
    public SavannaSourceMapping(string sourcePath, TextSpan sourceSpan, TextSpan generatedSpan)
    {
        SourcePath    = sourcePath;
        SourceSpan    = sourceSpan;
        GeneratedSpan = generatedSpan;
    }
}
