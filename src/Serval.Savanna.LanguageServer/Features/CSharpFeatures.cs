#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.QuickInfo;
using Microsoft.CodeAnalysis.Text;
using Serval.Savanna.Compiler.CodeGeneration;
using Serval.Savanna.Compiler.Syntax;
using Serval.Savanna.LanguageServer.Rpc;
using Serval.Savanna.LanguageServer.Workspace;

namespace Serval.Savanna.LanguageServer.Features;

public sealed partial class CSharpFeatures
{
    private readonly Document               _document;
    private readonly SavannaGeneratedSource _projection;
    private readonly SavannaDocument        _template;

    public CSharpFeatures(TemplateSnapshot snapshot)
    {
        _template   = snapshot.Document;
        _projection = SavannaProjection.Create(_template);
        _document   = snapshot.Project!.AddDocument("__SavannaEditorProjection.cs", SourceText.From(_projection.Text), filePath: _template.Path + ".editor.cs");
    }

    public async Task<object[]> CompleteAsync(int offset, CancellationToken cancellationToken)
    {
        var mapped  = MapOffset(offset);
        var service = CompletionService.GetService(_document);
        if (mapped is null || service is null)
        {
            return [];
        }

        var completion = await service.GetCompletionsAsync(_document, mapped.Value, cancellationToken: cancellationToken);
        var items      = new List<object>();
        var typed      = _projection.Text.Substring(completion.Span.Start, Math.Max(0, mapped.Value - completion.Span.Start));
        foreach (var item in completion.ItemsList.Where(item => item.FilterText.StartsWith(typed, StringComparison.OrdinalIgnoreCase)).Take(300))
        {
            var change = await service.GetChangeAsync(_document, item, cancellationToken: cancellationToken);
            var span   = MapSpan(change.TextChange.Span);
            if (span is null)
            {
                continue;
            }

            items.Add(new
            {
                label      = item.DisplayText,
                detail     = item.InlineDescription,
                sortText   = item.SortText,
                filterText = item.FilterText,
                kind       = CompletionKind(item.Tags),
                textEdit = new
                {
                    range   = Position.Range(_template.Text, span.Value),
                    newText = change.TextChange.NewText
                },
            });
        }

        return [.. items];
    }

    public async Task<object?> HoverAsync(int offset, CancellationToken cancellationToken)
    {
        var mapped  = MapOffset(offset);
        var service = QuickInfoService.GetService(_document);
        if (mapped is null || service is null)
        {
            return null;
        }

        var info = await service.GetQuickInfoAsync(_document, mapped.Value, cancellationToken);
        var span = info is null ? null : MapSpan(info.Span);
        if (info is null || span is null)
        {
            return null;
        }

        var (symbol, _) = await SymbolAtAsync(mapped.Value, cancellationToken);
        if (symbol is IFieldSymbol field && IsProjectionField(field))
        {
            var description = field.Name switch
            {
                "Model" => "The model passed to this template.",
                "Urls"  => "Serval's reverse routing service for named routes.",
                _       => "The current render context, including services, cancellation and formatting.",
            };
            return new
            {
                contents = new
                {
                    kind  = "plaintext",
                    value = field.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat) + " " + field.Name + "\n\n" + description
                },
                range = Position.Range(_template.Text, span.Value),
            };
        }

        return new
        {
            contents = new
            {
                kind  = "plaintext",
                value = string.Join("\n\n", info.Sections.Select(section => string.Concat(section.TaggedParts.Select(part => part.Text))))
            },
            range = Position.Range(_template.Text, span.Value),
        };
    }

    public async Task<object?> DefinitionAsync(int offset, CancellationToken cancellationToken)
    {
        var mapped = MapOffset(offset);
        if (mapped is null)
        {
            return null;
        }

        var (symbol, _) = await SymbolAtAsync(mapped.Value, cancellationToken);
        if (symbol is IFieldSymbol field && IsProjectionField(field))
        {
            symbol = field.Type;
        }

        if (symbol is null)
        {
            return null;
        }

        symbol = await SymbolFinder.FindSourceDefinitionAsync(symbol, _document.Project.Solution, cancellationToken) ?? symbol;
        var location = symbol.Locations.FirstOrDefault(location => location.IsInSource);
        if (location?.SourceTree is null)
        {
            return await MetadataDeclaration.CreateAsync(symbol, cancellationToken);
        }

        if (location.SourceTree == await _document.GetSyntaxTreeAsync(cancellationToken))
        {
            var span = MapSpan(location.SourceSpan);
            return span is null
                ? null
                : new
                {
                    uri   = new Uri(_template.Path).AbsoluteUri,
                    range = Position.Range(_template.Text, span.Value)
                };
        }

        var path = location.SourceTree.FilePath;
        if (!Path.IsPathFullyQualified(path) || !File.Exists(path))
        {
            return null;
        }

        return new
        {
            uri   = new Uri(path).AbsoluteUri,
            range = Position.Range(await location.SourceTree.GetTextAsync(cancellationToken), location.SourceSpan)
        };
    }

    public async Task<object[]> DiagnosticsAsync(CancellationToken cancellationToken)
    {
        var model = await _document.GetSemanticModelAsync(cancellationToken);
        return
        [
            .. model!.GetDiagnostics(cancellationToken: cancellationToken)
                     .Where(diagnostic => diagnostic.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning && diagnostic.Location.SourceTree == model.SyntaxTree)
                     .Select(diagnostic => (Diagnostic: diagnostic, Span: MapSpan(diagnostic.Location.SourceSpan)))
                     .Where(item => item.Span is not null)
                     .Select(item => (object)new
                     {
                         range    = Position.Range(_template.Text, item.Span!.Value),
                         severity = item.Diagnostic.Severity == DiagnosticSeverity.Error ? 1 : 2,
                         code     = item.Diagnostic.Id,
                         source   = "C#",
                         message  = item.Diagnostic.GetMessage(),
                     })
        ];
    }

    private async Task<(ISymbol? Symbol, TextSpan Span)> SymbolAtAsync(int offset, CancellationToken cancellationToken)
    {
        var root  = (await _document.GetSyntaxRootAsync(cancellationToken))!;
        var model = (await _document.GetSemanticModelAsync(cancellationToken))!;
        var token = root.FindToken(offset);
        if (token.SpanStart == offset && offset > 0)
        {
            var previous = root.FindToken(offset - 1);
            if (previous.Span.End == offset && previous.RawKind == (int)SyntaxKind.IdentifierToken && token.RawKind != previous.RawKind)
            {
                token = previous;
            }
        }

        var      node   = token.Parent;
        ISymbol? symbol = null;
        while (node is not null && symbol is null)
        {
            symbol = model.GetSymbolInfo(node, cancellationToken).Symbol ?? model.GetDeclaredSymbol(node, cancellationToken);
            node   = node.Parent;
        }

        return (symbol, token.Span);
    }

    private int? MapOffset(int offset)
    {
        var mapping = _projection.Mappings.FirstOrDefault(mapping => offset >= mapping.SourceSpan.Start && offset <= mapping.SourceSpan.End);
        return mapping is null ? null : mapping.GeneratedSpan.Start + offset - mapping.SourceSpan.Start;
    }

    private TextSpan? MapSpan(TextSpan generated)
    {
        var mapping = _projection.Mappings.FirstOrDefault(mapping => mapping.GeneratedSpan.Contains(generated));
        return mapping is null ? null : new TextSpan(mapping.SourceSpan.Start + generated.Start - mapping.GeneratedSpan.Start, generated.Length);
    }

    private static bool IsProjectionField(IFieldSymbol field) => field.Name is "Model" or "Context" or "Urls" && field.ContainingType.Name == "__SavannaEditorProjection";

    private static int CompletionKind(IEnumerable<string> tags)
    {
        foreach (var tag in tags)
        {
            switch (tag)
            {
                case "Method":
                    return 2;
                case "Property":
                    return 10;
                case "Field":
                    return 5;
                case "Class":
                    return 7;
                case "Interface":
                    return 8;
                case "Keyword":
                    return 14;
                case "Namespace":
                    return 9;
                case "Local":
                case "Parameter":
                    return 6;
            }
        }

        return 1;
    }
}
