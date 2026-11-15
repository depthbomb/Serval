#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.CodeAnalysis.Text;

namespace Serval.Savanna.Compiler.Syntax;

/// <summary>Represents a parsed body item or control-flow marker.</summary>
public sealed class SavannaNode
{
    /// <summary>The rendering or control-flow operation.</summary>
    public SavannaNodeKind Kind { get; }

    /// <summary>The primary text, expression, or directive value.</summary>
    public string Value { get; }

    /// <summary>The primary value's UTF-16 source range.</summary>
    public TextSpan Span { get; }

    /// <summary>The optional secondary expression or argument.</summary>
    public string Argument { get; }

    /// <summary>The secondary argument's UTF-16 source range.</summary>
    public TextSpan ArgumentSpan { get; }

    /// <summary>Whether the construct has a body and a matching closing directive.</summary>
    public bool Paired { get; internal set; }

    /// <summary>Whether a block or slot must be supplied by its caller or derived view.</summary>
    public bool Required { get; internal set; }

    /// <summary>Creates a body item with its values and source ranges.</summary>
    /// <param name="kind">The node kind.</param>
    /// <param name="value">The primary text, expression, or directive value.</param>
    /// <param name="span">The UTF-16 source range for the primary value.</param>
    /// <param name="argument">An optional secondary expression or argument.</param>
    /// <param name="argumentSpan">The secondary argument's source range, when present.</param>
    public SavannaNode(SavannaNodeKind kind, string value, TextSpan span, string argument = "", TextSpan argumentSpan = default)
    {
        Kind         = kind;
        Value        = value;
        Span         = span;
        Argument     = argument;
        ArgumentSpan = argumentSpan;
    }
}
