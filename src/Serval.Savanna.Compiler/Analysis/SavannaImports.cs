#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Serval.Savanna.Compiler.Syntax;

namespace Serval.Savanna.Compiler.Analysis;

/// <summary>Resolves namespace imports inherited through _Imports.svn files.</summary>
public static class SavannaImports
{
    /// <summary>Merges ancestor namespace imports into each document without modifying the inputs.</summary>
    /// <param name="documents">The templates and imports files in the project.</param>
    /// <returns>
    ///     The input list when no imports files exist; otherwise copied documents with deduplicated namespace imports.
    /// </returns>
    /// <remarks>
    ///     Imports are applied from outer folders inward, followed by local using declarations. Other syntax collections
    ///     remain shared.
    /// </remarks>
    public static IReadOnlyList<SavannaDocument> Apply(IReadOnlyList<SavannaDocument> documents)
    {
        var imports = documents.Where(document => document.IsImports).ToArray();
        if (imports.Length == 0)
        {
            return documents;
        }

        return
        [
            .. documents.Select(document =>
            {
                var inherited = imports.Where(import => document.Name.StartsWith(import.Name.Substring(0, import.Name.Length - "_Imports.svn".Length), StringComparison.Ordinal))
                                       .OrderBy(import => import.Name.Length).SelectMany(import => import.Usings);
                return new SavannaDocument(document.Path, document.Name, document.Text)
                {
                    Model     = document.Model,
                    ModelSpan = document.ModelSpan,
                    IsLayout  = document.IsLayout,
                    Parent    = document.Parent,
                    Imports   = document.Imports,
                    Body      = document.Body,
                    Blocks    = document.Blocks,
                    Usings =
                    [
                        .. inherited.Concat(document.Usings)
                                    .Distinct(StringComparer.Ordinal),
                    ],
                    Diagnostics = document.Diagnostics,
                };
            }),
        ];
    }
}
