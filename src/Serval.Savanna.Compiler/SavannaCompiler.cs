#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Serval.Savanna.Compiler.Analysis;
using Serval.Savanna.Compiler.CodeGeneration;
using Serval.Savanna.Compiler.Parsing;
using Serval.Savanna.Compiler.Syntax;

namespace Serval.Savanna.Compiler;

/// <summary>Validates a set of Savanna templates and emits their runtime C# views.</summary>
public static class SavannaCompiler
{
    /// <summary>Applies imports, validates template dependencies, and emits concrete views.</summary>
    /// <param name="documents">Parsed templates, including layouts and _Imports.svn documents.</param>
    /// <param name="generatedNamespace">The valid C# namespace under which generated views are declared.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>
    ///     Generated sources and diagnostics; sources are empty when any diagnostic prevents compilation.
    /// </returns>
    /// <remarks>
    ///     Names are exact, case-sensitive paths relative to the Savanna root. Layouts are validated but are not emitted as
    ///     standalone views; cyclic or excessively deep dependencies are rejected.
    /// </remarks>
    /// <exception cref="OperationCanceledException">Compilation is canceled.</exception>
    public static SavannaCompilation Compile(IReadOnlyList<SavannaDocument> documents,
                                          string                      generatedNamespace,
                                          CancellationToken           cancellationToken = default)
    {
        var diagnostics = documents.SelectMany(document => document.Diagnostics).ToList();
        documents = [.. SavannaImports.Apply(documents).Where(document => !document.IsImports)];
        var sources = new List<SavannaGeneratedSource>();
        if (string.IsNullOrWhiteSpace(generatedNamespace) || generatedNamespace.Split('.').Any(part => !SyntaxFacts.IsValidIdentifier(part)))
        {
            diagnostics.Add(new SavannaDiagnostic("SAVANNA004", "SavannaNamespace must be a valid C# namespace.", "", default));
            return new SavannaCompilation(sources, diagnostics);
        }

        var views       = new Dictionary<string, SavannaDocument>(StringComparer.Ordinal);
        var identifiers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var document in documents.OrderBy(document => document.Name, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!SavannaParser.TryPath(SymbolDisplay.FormatLiteral(document.Name, true), out _) || !document.Name.EndsWith(".svn", StringComparison.Ordinal))
            {
                Error(document, default, "A template must have an exact, project-relative .svn path.", diagnostics);
                continue;
            }

            var identifier = IdentifierPath(document.Name);
            if (views.ContainsKey(document.Name) || !identifiers.Add(identifier))
            {
                Error(document, default, "Duplicate template name or generated C# identifier: " + document.Name, diagnostics);
                continue;
            }

            views.Add(document.Name, document);
        }

        // Index folder prefixes once; comparing every view against every other view gets expensive quickly.
        var folders = new HashSet<string>(StringComparer.Ordinal);
        foreach (var identifier in identifiers)
        {
            for (var separator = identifier.IndexOf('.'); separator >= 0; separator = identifier.IndexOf('.', separator + 1))
            {
                folders.Add(identifier.Substring(0, separator));
            }
        }

        foreach (var document in views.Values)
        {
            var identifier = IdentifierPath(document.Name);
            if (folders.Contains(identifier))
            {
                Error(document, default, "A view name conflicts with a view folder: " + document.Name, diagnostics);
            }

            foreach (var import in document.Imports.Values)
            {
                if (!views.ContainsKey(import))
                {
                    Error(document, default, "Imported view not found: " + import, diagnostics);
                }
            }
        }

        if (diagnostics.Count > 0)
        {
            return new SavannaCompilation(sources, diagnostics);
        }

        var dependencies  = new Dictionary<string, List<SavannaDocument>>(StringComparer.Ordinal);
        var slots         = views.Values.ToDictionary(view => view.Name, view => SavannaSlotsAnalysis.Analyze(view, diagnostics), StringComparer.Ordinal);
        var slotContracts = new Dictionary<string, IReadOnlyList<SavannaNode>>(StringComparer.Ordinal);
        foreach (var document in views.Values)
        {
            var targets = new List<SavannaDocument>();
            if (document.Parent is { } parent)
            {
                var target = Resolve(document, parent, views, diagnostics);
                if (target is not null)
                {
                    targets.Add(target);
                }
            }

            foreach (var node in document.Body.Concat(document.Blocks.SelectMany(block => block.Body)).Where(node => node.Kind == SavannaNodeKind.Include))
            {
                var target = Resolve(document, node, views, diagnostics);
                if (target is not null)
                {
                    targets.Add(target);
                    if (target.IsLayout)
                    {
                        Error(document, node.Span, "A layout cannot be included as a concrete view.", diagnostics);
                    }
                }
            }

            dependencies.Add(document.Name, targets);
            foreach (var call in slots[document.Name].Calls.Where(call => call.Node.Kind == SavannaNodeKind.Include))
            {
                var target = Resolve(document, call.Node, views, diagnostics);
                if (target is null)
                {
                    continue;
                }

                if (!slotContracts.TryGetValue(target.Name, out var declarations))
                {
                    var targetName = target.Name;
                    var chain      = new List<SavannaDocument>();
                    var seen       = new HashSet<string>(StringComparer.Ordinal);
                    while (target is not null && seen.Add(target.Name) && chain.Count < 128)
                    {
                        chain.Add(target);
                        target = target.Parent is null ? null : Resolve(target, target.Parent, views, diagnostics);
                    }

                    // An overridden block's unused slots aren't part of the concrete view's contract.
                    var rendered = SavannaSlotsAnalysis.RenderedNodes(chain);
                    declarations = chain.SelectMany(view => slots[view.Name].Declarations).Where(rendered.Contains).ToArray();
                    slotContracts.Add(targetName, declarations);
                }

                foreach (var required in declarations.Where(slot => slot.Required && !call.Supplied.Contains(slot.Value)))
                {
                    Error(document, call.Node.Span, "Required slot not supplied: " + required.Value, diagnostics);
                }

                foreach (var supplied in call.Supplied.Where(name => declarations.All(slot => slot.Value != name)))
                {
                    Error(document, call.Node.Span, "Unknown slot: " + supplied, diagnostics);
                }
            }
        }

        var active  = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);

        bool Visit(SavannaDocument document)
        {
            // Check the active path before the visited set: a back-edge is already visited but is still a cycle.
            if (active.Contains(document.Name) || active.Count >= 128)
            {
                Error(document, document.Parent?.Span ?? default, "Cyclic or excessively deep template dependency: " + document.Name, diagnostics);
                return false;
            }

            if (!visited.Add(document.Name))
            {
                return true;
            }

            active.Add(document.Name);
            foreach (var target in dependencies[document.Name])
            {
                if (!Visit(target))
                {
                    active.Remove(document.Name);
                    return false;
                }
            }

            active.Remove(document.Name);
            return true;
        }

        foreach (var document in views.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Visit(document);
        }

        if (diagnostics.Count > 0)
        {
            return new SavannaCompilation(sources, diagnostics);
        }

        foreach (var document in views.Values.OrderBy(document => document.Name, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chain = new List<SavannaDocument> { document };
            while (chain[chain.Count - 1].Parent is { } parent)
            {
                chain.Add(Resolve(chain[chain.Count - 1], parent, views, diagnostics)!);
            }

            for (var index = 0; index < chain.Count - 1; index++)
            {
                var inherited = new HashSet<string>(chain.Skip(index + 1).SelectMany(ancestor => ancestor.Blocks).Select(block => block.Name), StringComparer.Ordinal);
                foreach (var block in chain[index].Blocks)
                {
                    if (!inherited.Contains(block.Name))
                    {
                        Error(chain[index], block.Span, "Unknown inherited block: " + block.Name, diagnostics);
                    }

                    if (block.Required)
                    {
                        Error(chain[index], block.Span, "Only an original block declaration can be required.", diagnostics);
                    }
                }
            }

            if (!document.IsLayout)
            {
                foreach (var block in chain.SelectMany(ancestor => ancestor.Blocks).GroupBy(block => block.Name).Select(group => group.First()).Where(block => block.Required))
                {
                    Error(document, document.ModelSpan, "Required block is not implemented: " + block.Name, diagnostics);
                }

                sources.Add(SavannaEmitter.Emit(chain, views, generatedNamespace, diagnostics, cancellationToken));
            }
            else
            {
                // Layouts also need diagnostics when no concrete view uses them yet.
                SavannaEmitter.ValidateMarkup(document, diagnostics);
            }
        }

        return new SavannaCompilation(diagnostics.Count == 0 ? sources : Array.Empty<SavannaGeneratedSource>(), diagnostics);
    }

    internal static SavannaDocument? Resolve(SavannaDocument document, SavannaNode node, IReadOnlyDictionary<string, SavannaDocument> views, List<SavannaDiagnostic> diagnostics)
    {
        if (!SavannaParser.TryPath(node.Value, out string path) && !document.Imports.TryGetValue(node.Value, out path!))
        {
            Error(document, node.Span, "Use a literal view path or a declared import alias.", diagnostics);
            return null;
        }

        if (!views.TryGetValue(path, out var target))
        {
            Error(document, node.Span, "View not found (paths are case-sensitive): " + path, diagnostics);
            return null;
        }

        return target;
    }

    internal static string IdentifierPath(string name)
        => string.Join(".", name.Substring(0, name.Length - ".svn".Length).Split('/').Select(Identifier));

    internal static string Identifier(string value)
    {
        var name = new string(value.Select(character => SyntaxFacts.IsIdentifierPartCharacter(character) ? character : '_').ToArray());
        if (name.Length == 0 || !SyntaxFacts.IsIdentifierStartCharacter(name[0]))
        {
            name = "_" + name;
        }

        return "@" + name;
    }

    internal static void Error(SavannaDocument document, TextSpan span, string message, List<SavannaDiagnostic> diagnostics)
        => diagnostics.Add(new SavannaDiagnostic("SAVANNA002", message, document.Path, span));
}
