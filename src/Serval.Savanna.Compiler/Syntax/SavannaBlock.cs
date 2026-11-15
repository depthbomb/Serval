#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.CodeAnalysis.Text;

namespace Serval.Savanna.Compiler.Syntax;

/// <summary>Represents a named layout block or an overriding implementation.</summary>
public sealed class SavannaBlock
{
    /// <summary>The case-sensitive block name.</summary>
    public string Name { get; }

    /// <summary>Whether the original declaration requires a concrete override.</summary>
    public bool Required { get; }

    /// <summary>The declaration's UTF-16 source range.</summary>
    public TextSpan Span { get; }

    /// <summary>The parsed body of this declaration or override.</summary>
    public IReadOnlyList<SavannaNode> Body { get; }

    /// <summary>Creates a named block from its parsed body and source location.</summary>
    /// <param name="name">The case-sensitive block name.</param>
    /// <param name="required">Whether an original declaration requires an override.</param>
    /// <param name="span">The declaration's source range.</param>
    /// <param name="body">The nodes within the block.</param>
    public SavannaBlock(string name, bool required, TextSpan span, IReadOnlyList<SavannaNode> body)
    {
        Name     = name;
        Required = required;
        Span     = span;
        Body     = body;
    }
}
