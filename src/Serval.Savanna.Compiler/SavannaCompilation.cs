#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Serval.Savanna.Compiler.CodeGeneration;

namespace Serval.Savanna.Compiler;

/// <summary>Contains the sources and diagnostics from compiling a template set.</summary>
public sealed class SavannaCompilation
{
    /// <summary>The generated sources; SavannaCompiler returns none when compilation has diagnostics.</summary>
    public IReadOnlyList<SavannaGeneratedSource> Sources { get; }

    /// <summary>The collected syntax, reference, markup, and configuration diagnostics.</summary>
    public IReadOnlyList<SavannaDiagnostic> Diagnostics { get; }

    /// <summary>Creates a compilation result from generated sources and diagnostics.</summary>
    /// <param name="sources">The emitted C# sources.</param>
    /// <param name="diagnostics">The collected template diagnostics.</param>
    public SavannaCompilation(IReadOnlyList<SavannaGeneratedSource> sources, IReadOnlyList<SavannaDiagnostic> diagnostics)
    {
        Sources     = sources;
        Diagnostics = diagnostics;
    }
}
