#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.CodeAnalysis.Text;

namespace Serval.Savanna.Compiler;

/// <summary>Reports a template syntax, reference, markup, or configuration error.</summary>
public sealed class SavannaDiagnostic
{
    /// <summary>The diagnostic identifier.</summary>
    public string Code { get; }

    /// <summary>The explanation of the error.</summary>
    public string Message { get; }

    /// <summary>The source path, or an empty string for project-level configuration.</summary>
    public string Path { get; }

    /// <summary>The UTF-16 source range; an empty range can mark an insertion point or project-level error.</summary>
    public TextSpan Span { get; }

    /// <summary>Creates a diagnostic tied to a template source range.</summary>
    /// <param name="code">The diagnostic identifier, such as SAVANNA001.</param>
    /// <param name="message">The explanation shown to the user.</param>
    /// <param name="path">The source path, or an empty string for project-level configuration.</param>
    /// <param name="span">The UTF-16 source range associated with the error.</param>
    public SavannaDiagnostic(string code, string message, string path, TextSpan span)
    {
        Code    = code;
        Message = message;
        Path    = path;
        Span    = span;
    }
}
