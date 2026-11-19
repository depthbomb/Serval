#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Serval.Savanna.Compiler.Parsing;
using Serval.Savanna.Compiler.Syntax;
using Serval.Savanna.LanguageServer.Rpc;
using Serval.Savanna.LanguageServer.Workspace;

namespace Serval.Savanna.LanguageServer.Features;

public static class TemplateFeatures
{
    private static readonly Dictionary<string, string> Directives = new()
    {
        ["model"]       = "model Type: declares the C# type of Model. Required in every template.",
        ["using"]       = "using Namespace: imports a C# namespace into this template.",
        ["layout"]      = "layout: marks a template as an abstract layout.",
        ["extends"]     = "extends \"Layouts/Site.svn\" with Model.Site: inherits a layout with an explicit model.",
        ["import"]      = "import \"Shared/Card.svn\" as Card: declares a template alias.",
        ["include"]     = "include Card with Model.Item: renders a partial with an explicit, typed model.",
        ["component"]   = "component ComponentType with Model.Arguments: renders an asynchronous component from the request service scope.",
        ["block"]       = "block Name [required]: declares or overrides a named layout block.",
        ["endblock"]    = "endblock: closes a named block.",
        ["parent"]      = "parent: renders the next inherited implementation of the current block.",
        ["if"]          = "if (condition): renders content when a C# condition is true.",
        ["unless"]      = "unless (condition): renders content when a C# condition is false. Supports elseif and else branches.",
        ["endunless"]   = "endunless: closes an unless statement.",
        ["debug"]       = "debug: compiles its content only when the DEBUG symbol is defined (#if DEBUG).",
        ["enddebug"]    = "enddebug: closes debug-only content.",
        ["release"]     = "release: compiles its content only when the DEBUG symbol is not defined (#if !DEBUG).",
        ["endrelease"]  = "endrelease: closes release-only content.",
        ["switch"]      = "switch (value): selects a case using C# switch semantics.",
        ["case"]        = "case value: matches a C# constant or pattern. Adjacent case labels can share a body.",
        ["default"]     = "default: renders when no other switch case matches.",
        ["break"]       = "break: exits the nearest switch or loop. The optional if attribute supplies a C# condition.",
        ["continue"]    = "continue: skips to the next iteration of the nearest loop. The optional if attribute supplies a C# condition.",
        ["endswitch"]   = "endswitch: closes a switch statement.",
        ["elseif"]      = "elseif (condition): tests another C# condition.",
        ["else"]        = "else: renders the alternative branch.",
        ["endif"]       = "endif: closes an if statement.",
        ["foreach"]     = "foreach (var item in Model.Items): iterates a C# sequence.",
        ["await"]       = "await foreach (var item in Model.Items): iterates an asynchronous C# sequence.",
        ["endforeach"]  = "endforeach: closes a foreach or await foreach loop.",
        ["for"]         = "for (var i = 0; i < count; i++): repeats a C# loop.",
        ["endfor"]      = "endfor: closes a for loop.",
        ["html"]        = "html expression: renders explicitly trusted SavannaHtml. Ordinary {{ expressions }} are HTML encoded.",
        ["verbatim"]    = "verbatim: renders everything literally until {% endverbatim %}.",
        ["endverbatim"] = "endverbatim: closes literal template content.",
    };

    public static object[]? Complete(TemplateSnapshot snapshot, int offset)
    {
        var document = snapshot.Document;
        var token    = TokenAt(document, offset);
        if (token?.Tag is {} tag)
        {
            var start = tag.Span.Start + (tag.Closing ? 3 : 2);
            if (offset <= start + tag.Name.Length)
            {
                var partial = document.Text.ToString(TextSpan.FromBounds(start, Math.Max(start, offset)));
                return
                [
                    .. SavannaTagRules.Attributes.Keys.Where(name => name.StartsWith(partial, StringComparison.Ordinal))
                                      .Select(name => Item(name, name, "Savanna " + name + " tag", 14, new TextSpan(start, partial.Length))),
                ];
            }

            var attribute = tag.Attributes.Values.FirstOrDefault(value => value.Span.Start <= offset && offset <= value.Span.End);
            if (attribute is not null)
            {
                var partial = document.Text.ToString(TextSpan.FromBounds(attribute.Span.Start, offset));
                var range   = new TextSpan(attribute.Span.Start, partial.Length);
                if (attribute.Name == "view")
                {
                    return
                    [
                        .. snapshot.Templates.Where(view => !view.IsImports && view.Name != document.Name && view.Name.StartsWith(partial, StringComparison.Ordinal))
                                   .Select(view => Item(view.Name, view.Name, "Model: " + view.Model, 17, range))
                                   .Concat(document.Imports.Where(pair => pair.Key.StartsWith(partial, StringComparison.Ordinal))
                                                   .Select(pair => Item(pair.Key, pair.Key, pair.Value, 18, range))),
                    ];
                }

                if (tag.Name == "block" && attribute.Name == "name")
                {
                    return
                    [
                        .. Ancestors(snapshot).SelectMany(parent => parent.Blocks).DistinctBy(block => block.Name)
                                              .Where(block => block.Name.StartsWith(partial, StringComparison.Ordinal))
                                              .Select(block => Item(block.Name, block.Name, "Inherited block", 6, range)),
                    ];
                }

                return null;
            }

            var wordStart = offset;
            while (wordStart > start && char.IsLetter(document.Text[wordStart - 1]))
            {
                wordStart--;
            }

            var prefixText = document.Text.ToString(TextSpan.FromBounds(wordStart, offset));
            return
            [
                .. SavannaTagRules.Attributes.GetValueOrDefault(tag.Name, []).Where(name => !tag.Attributes.ContainsKey(name) && name.StartsWith(prefixText, StringComparison.Ordinal))
                                  .Select(name => Item(name, name is "required" or "await" ? name : name + "=\"\"", "Savanna " + tag.Name + " attribute", 10, new TextSpan(wordStart, prefixText.Length))),
            ];
        }

        if (token?.Kind != SavannaTokenKind.Directive)
        {
            return null;
        }

        var prefix = document.Text.ToString(TextSpan.FromBounds(token.Span.Start, offset));
        var split  = prefix.IndexOfAny([' ', '\t', '\r', '\n']);
        if (split < 0)
        {
            return
            [
                .. Directives.Where(pair => pair.Key.StartsWith(prefix, StringComparison.Ordinal))
                             .Select(pair => Item(pair.Key, pair.Key, pair.Value, 14, new TextSpan(token.Span.Start, prefix.Length))),
            ];
        }

        var command       = prefix[..split];
        var argumentStart = token.Span.Start + split;
        while (argumentStart < offset && char.IsWhiteSpace(document.Text[argumentStart]))
        {
            argumentStart++;
        }

        var argument = document.Text.ToString(TextSpan.FromBounds(argumentStart, offset));
        if (command is "extends" or "include" or "import")
        {
            if (argument.StartsWith('"') && argument.AsSpan(1).IndexOf('"') < 0)
            {
                var partial = argument[1..];
                return
                [
                    .. snapshot.Templates.Where(view => view.Name != document.Name && (command != "extends" || view.IsLayout) && view.Name.StartsWith(partial, StringComparison.Ordinal))
                               .Select(view => Item(view.Name, view.Name, "Model: " + view.Model, 17, new TextSpan(argumentStart + 1, partial.Length))),
                ];
            }

            if (command != "import" && !argument.Contains(' ') && !argument.StartsWith('"'))
            {
                return
                [
                    .. document.Imports.Where(pair => pair.Key.StartsWith(argument, StringComparison.Ordinal))
                               .Select(pair => Item(pair.Key, pair.Key, pair.Value, 18, new TextSpan(argumentStart, argument.Length))),
                ];
            }
        }

        if (command == "block" && !argument.Contains(' '))
        {
            return
            [
                .. Ancestors(snapshot).SelectMany(parent => parent.Blocks).DistinctBy(block => block.Name)
                                      .Where(block => block.Name.StartsWith(argument, StringComparison.Ordinal))
                                      .Select(block => Item(block.Name, block.Name, block.Required ? "Required inherited block" : "Inherited block", 6, new TextSpan(argumentStart, argument.Length))),
            ];
        }

        return null;

        object Item(string label, string insert, string detail, int kind, TextSpan span)
        {
            return new
            {
                label,
                kind,
                detail,
                textEdit = new
                {
                    range   = Position.Range(document.Text, span),
                    newText = insert,
                },
            };
        }
    }

    public static object? Hover(TemplateSnapshot snapshot, int offset)
    {
        var token = TokenAt(snapshot.Document, offset);
        if (token?.Tag is {} tag)
        {
            var start = tag.Span.Start + (tag.Closing ? 3 : 2);
            if (offset >= start && offset <= start + tag.Name.Length && SavannaTagRules.Attributes.TryGetValue(tag.Name, out var attributes))
            {
                var tagDescription = tag.Name is "unless" or "debug" or "release" or "switch" or "case" or "default" or "break" or "continue"
                    ? Directives[tag.Name]
                    : "Savanna " + tag.Name + ". Attributes: " + string.Join(", ", attributes);
                return new
                {
                    contents = new
                    {
                        kind  = "plaintext",
                        value = tagDescription,
                    },
                    range = Position.Range(snapshot.Document.Text, new TextSpan(start, tag.Name.Length)),
                };
            }

            return null;
        }

        if (token?.Kind != SavannaTokenKind.Directive)
        {
            return null;
        }

        var command = token.Value.Split(' ', '\t', '\r', '\n')[0];
        if (offset <= token.Span.Start + command.Length && Directives.TryGetValue(command, out var description))
        {
            return new
            {
                contents = new
                {
                    kind  = "plaintext",
                    value = description,
                },
                range = Position.Range(snapshot.Document.Text, new TextSpan(token.Span.Start, command.Length)),
            };
        }

        return null;
    }

    public static object? Definition(TemplateSnapshot snapshot, int offset)
    {
        var document = snapshot.Document;
        var token    = TokenAt(document, offset);
        if (token?.Tag is {} tag)
        {
            if (tag.Get("view") is {} viewAttribute && viewAttribute.Span.Start <= offset && offset <= viewAttribute.Span.End)
            {
                var name = document.Imports.GetValueOrDefault(viewAttribute.Value, viewAttribute.Value);
                var view = snapshot.Templates.FirstOrDefault(view => view.Name == name);
                return view is null ? null : Location(view, default);
            }

            if (tag.Name is "block" or "parent")
            {
                var name = tag.Get("name")?.Value ?? document.Blocks.LastOrDefault(block => block.Span.Start < offset)?.Name;
                foreach (var parent in Ancestors(snapshot))
                {
                    var block = parent.Blocks.FirstOrDefault(block => block.Name == name);
                    if (block is not null)
                    {
                        return Location(parent, new TextSpan(block.Span.Start, block.Name.Length));
                    }
                }
            }

            return null;
        }

        if (token?.Kind != SavannaTokenKind.Directive)
        {
            return null;
        }

        var split   = token.Value.IndexOfAny([' ', '\t', '\r', '\n']);
        var command = split < 0 ? token.Value : token.Value[..split];
        if (command is "extends" or "include" or "import")
        {
            var argument = token.Value[command.Length..].TrimStart();
            var target   = SyntaxFactory.ParseTokens(argument).FirstOrDefault();
            var start    = token.Span.End - argument.Length + target.SpanStart;
            if (offset < start || offset > start + target.Span.Length)
            {
                return null;
            }

            var name = target.IsKind(SyntaxKind.StringLiteralToken) ? target.ValueText : document.Imports.GetValueOrDefault(target.Text);
            var view = snapshot.Templates.FirstOrDefault(view => view.Name == name);
            if (view is not null)
            {
                return Location(view, default);
            }
        }

        if (command is "block" or "parent")
        {
            var name = command == "block"
                ? token.Value[command.Length..].Trim().Split(' ')[0]
                : document.Blocks.LastOrDefault(block => block.Span.Start < offset)?.Name;
            foreach (var parent in Ancestors(snapshot))
            {
                var block = parent.Blocks.FirstOrDefault(block => block.Name == name);
                if (block is not null)
                {
                    return Location(parent, new TextSpan(block.Span.Start, block.Name.Length));
                }
            }
        }

        return null;
    }

    public static SavannaToken? TokenAt(SavannaDocument document, int offset) => SavannaLexer.Tokenize(document, [], CancellationToken.None, true)
                                                                                             .FirstOrDefault(token => token.Span.Start <= offset && token.Span.End >= offset && token.Kind != SavannaTokenKind.Text);

    private static object Location(SavannaDocument document, TextSpan span) => new
    {
        uri   = new Uri(document.Path).AbsoluteUri,
        range = Position.Range(document.Text, span),
    };

    private static IEnumerable<SavannaDocument> Ancestors(TemplateSnapshot snapshot)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal)
        {
            snapshot.Document.Name,
        };
        var current = snapshot.Document;
        while (current.Parent is not null)
        {
            var value = current.Parent.Value;
            var name = SyntaxFactory.ParseExpression(value) is LiteralExpressionSyntax literal
                ? literal.Token.ValueText
                : current.Imports.GetValueOrDefault(value);
            var parent = snapshot.Templates.FirstOrDefault(view => view.Name == name);
            if (parent is null || !seen.Add(parent.Name))
            {
                yield break;
            }

            yield return parent;
            current = parent;
        }
    }
}
