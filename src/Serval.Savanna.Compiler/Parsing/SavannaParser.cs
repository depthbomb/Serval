#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Serval.Savanna.Compiler.Analysis;
using Serval.Savanna.Compiler.Syntax;

namespace Serval.Savanna.Compiler.Parsing;

/// <summary>Parses Savanna syntax into documents with source ranges and diagnostics.</summary>
public static class SavannaParser
{
    /// <summary>Parses one template without resolving other templates.</summary>
    /// <param name="path">The source path used in diagnostics and mappings.</param>
    /// <param name="name">The case-sensitive path relative to the Savanna root, with forward slashes.</param>
    /// <param name="text">The template's source text.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>A document containing parsed declarations, body nodes, blocks, and any syntax diagnostics.</returns>
    /// <remarks>
    ///     Recoverable syntax errors are returned as diagnostics rather than thrown. Cross-template references are checked
    ///     by SavannaCompiler.
    /// </remarks>
    public static SavannaDocument Parse(string path, string name, SourceText text, CancellationToken cancellationToken = default)
    {
        var     document    = new SavannaDocument(path, name, text);
        var     diagnostics = new List<SavannaDiagnostic>();
        var     tokens      = SavannaLexer.Tokenize(document, diagnostics, cancellationToken);
        var     root        = new List<SavannaNode>();
        var     body        = root;
        var     blocks      = new List<SavannaBlock>();
        var     imports     = new Dictionary<string, string>(StringComparer.Ordinal);
        var     usings      = new List<string>();
        var     scopes      = new Stack<(string Kind, bool HasElse)>();
        string? blockName   = null;
        var     blockSpan   = default(TextSpan);
        var     required    = false;
        var     modelSeen   = false;
        var     contentSeen = false;

        void Error(string message, TextSpan span)
        {
            diagnostics.Add(new SavannaDiagnostic("SAVANNA001", message, path, span));
        }

        foreach (var token in tokens)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (token.Kind == SavannaTokenKind.Text)
            {
                // A switch needs its first label before it can render anything. Jumps leave no place for trailing indentation.
                if (string.IsNullOrWhiteSpace(token.Value) &&
                    (scopes.Count > 0 && scopes.Peek().Kind == "switch" || body.LastOrDefault() is { Kind: SavannaNodeKind.Break or SavannaNodeKind.Continue, Value.Length: 0 }))
                {
                    continue;
                }

                if (scopes.Count > 0 && scopes.Peek().Kind == "switch")
                {
                    Error("Switch content must follow a case or default label.", token.Span);
                    continue;
                }

                body.Add(new SavannaNode(SavannaNodeKind.Text, token.Value, token.Span));
                contentSeen |= !string.IsNullOrWhiteSpace(token.Value);
                continue;
            }

            if (token.Kind == SavannaTokenKind.Expression)
            {
                if (scopes.Count > 0 && scopes.Peek().Kind == "switch")
                {
                    Error("Switch content must follow a case or default label.", token.Span);
                    continue;
                }

                if (token.Value.Length == 0)
                {
                    Error("An interpolation needs a C# expression.", token.Span);
                }

                body.Add(new SavannaNode(SavannaNodeKind.Expression, token.Value, token.Span));
                contentSeen = true;
                continue;
            }

            // Tag tokens already have a command and exact attribute spans. Only legacy directives need splitting.
            var command   = token.Value;
            var valueSpan = token.Span;
            var value     = "";
            if (token.Tag is null)
            {
                var split = token.Value.IndexOfAny([' ', '\t', '\r', '\n']);
                command   = split < 0 ? token.Value : token.Value.Substring(0, split);
                valueSpan = SavannaLexer.Trim(text.ToString(), TextSpan.FromBounds(token.Span.Start + command.Length, token.Span.End));
                value     = text.ToString(valueSpan);
            }

            if (scopes.Count > 0 && scopes.Peek().Kind == "switch" && command is not ("case" or "default" or "endswitch"))
            {
                Error("Switch content must follow a case or default label.", token.Span);
                continue;
            }

            if (token.Tag is {} tag)
            {
                // Both syntaxes feed the same node model, but tag attributes keep their own exact source spans.
                var primary   = SavannaTagRules.Primary(tag.Name);
                var attribute = primary is null ? null : tag.Get(primary);
                value     = attribute?.Value ?? "";
                valueSpan = attribute?.Span  ?? tag.Span;
                SavannaTagRules.Validate(tag, path, diagnostics);
                if (!tag.Closing)
                {
                    if (command == "import")
                    {
                        value = SymbolDisplay.FormatLiteral(value, true) + " as " + tag.Get("as")?.Value;
                    }

                    if (command == "block" && tag.Get("required") is not null)
                    {
                        value += " required";
                    }

                    if (command is "extends" or "include" or "component")
                    {
                        var argument = tag.Get(command == "component" ? "arguments" : "model");
                        var target   = command == "component" || !value.EndsWith(".svn", StringComparison.Ordinal) ? value : SymbolDisplay.FormatLiteral(value, true);
                        var call = new SavannaNode(command == "component" ? SavannaNodeKind.Component : SavannaNodeKind.Include, target, valueSpan,
                            argument?.Value ?? "Model", argument?.Span ?? valueSpan)
                        {
                            Paired = !tag.SelfClosing && command != "extends"
                        };
                        if (command == "extends")
                        {
                            if (contentSeen || document.Parent is not null)
                            {
                                Error("Declare one parent before rendered content.", tag.Span);
                            }

                            document.Parent = call;
                        }
                        else
                        {
                            body.Add(call);
                            if (call.Paired)
                            {
                                scopes.Push((command, false));
                            }

                            contentSeen = true;
                        }

                        continue;
                    }

                    if (command == "foreach")
                    {
                        var source = tag.Get("in");
                        scopes.Push(("foreach", false));
                        body.Add(new SavannaNode(tag.Get("await") is null ? SavannaNodeKind.ForEach : SavannaNodeKind.AwaitForEach,
                            value, valueSpan, source?.Value ?? "", source?.Span ?? valueSpan));
                        contentSeen = true;
                        continue;
                    }

                    if (command is "let" or "json")
                    {
                        var expression = tag.Get("value");
                        if (command == "let" && (!SyntaxFacts.IsValidIdentifier(value) || value is "Model" or "Context" or "Urls" || value.StartsWith("__", StringComparison.Ordinal)))
                        {
                            Error("A local needs a valid name that is not reserved by Savanna.", valueSpan);
                        }

                        body.Add(new SavannaNode(command == "let" ? SavannaNodeKind.Let : SavannaNodeKind.Json, value, valueSpan, expression?.Value ?? "", expression?.Span ?? valueSpan));
                        contentSeen = true;
                        continue;
                    }

                    if (command is "whitespace" or "slot")
                    {
                        if (command == "whitespace" && value != "preserve")
                        {
                            Error("The whitespace mode must be preserve.", valueSpan);
                        }

                        body.Add(new SavannaNode(command == "slot" ? SavannaNodeKind.Slot : SavannaNodeKind.Whitespace, value, valueSpan)
                        {
                            Paired   = !tag.SelfClosing,
                            Required = tag.Get("required") is not null
                        });
                        if (!tag.SelfClosing)
                        {
                            scopes.Push((command, false));
                        }

                        contentSeen = true;
                        continue;
                    }
                }
            }

            var metadata = command is "model" or "using" or "extends" or "import" or "layout";
            if (metadata && (contentSeen || blockName is not null || scopes.Count > 0))
            {
                Error("Template declarations must appear before rendered content and blocks.", token.Span);
                continue;
            }

            switch (command)
            {
                case "model":
                    if (modelSeen || value.Length == 0 || SyntaxFactory.ParseTypeName(value).ContainsDiagnostics)
                    {
                        Error("Declare exactly one valid model type.", valueSpan);
                    }

                    modelSeen          = true;
                    document.Model     = value;
                    document.ModelSpan = valueSpan;
                    break;
                case "using":
                    if (value.Length == 0 || value.Split('.').Any(part => !SyntaxFacts.IsValidIdentifier(part)))
                    {
                        Error("A using declaration requires a namespace name.", valueSpan);
                    }
                    else
                    {
                        usings.Add(value);
                    }

                    break;
                case "layout":
                    if (value.Length != 0 || document.IsLayout)
                    {
                        Error("Use one argument-free layout declaration.", token.Span);
                    }

                    document.IsLayout = true;
                    break;
                case "extends":
                    if (document.Parent is not null)
                    {
                        Error("A template can extend only one layout.", token.Span);
                    }

                    document.Parent = ParseCall(document, value, valueSpan, SavannaNodeKind.Include, diagnostics);
                    break;
                case "import":
                    var aliasSplit = value.LastIndexOf(" as ", StringComparison.Ordinal);
                    if (aliasSplit < 0 || !TryPath(value.Substring(0, aliasSplit).Trim(), out var importPath)
                                       || !SyntaxFacts.IsValidIdentifier(value.Substring(aliasSplit + 4).Trim()))
                    {
                        Error("Use import \"path.svn\" as Alias.", valueSpan);
                        break;
                    }

                    var alias = value.Substring(aliasSplit + 4).Trim();
                    if (imports.ContainsKey(alias))
                    {
                        Error("An import alias is already declared: " + alias, valueSpan);
                    }
                    else
                    {
                        imports.Add(alias, importPath);
                    }

                    break;
                case "block":
                    if (blockName is not null || scopes.Count > 0)
                    {
                        Error("Blocks must be declared at template top level.", token.Span);
                        break;
                    }

                    var parts = value.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length is < 1 or > 2 || !SyntaxFacts.IsValidIdentifier(parts[0]) || parts.Length == 2 && parts[1] != "required")
                    {
                        Error("Use block Name, optionally followed by required.", valueSpan);
                        break;
                    }

                    blockName = parts[0];
                    blockSpan = valueSpan;
                    required  = parts.Length == 2;
                    // Keep a placeholder in the outer body; the selected override is resolved later.
                    body      = [];
                    root.Add(new SavannaNode(SavannaNodeKind.Block, blockName, valueSpan));
                    contentSeen = true;
                    break;
                case "endblock":
                    if (blockName is null || scopes.Count > 0)
                    {
                        Error("Unexpected endblock or an unclosed control structure.", token.Span);
                        break;
                    }

                    if (value.Length != 0)
                    {
                        Error("endblock does not take arguments.", valueSpan);
                    }

                    if (blocks.Any(block => block.Name == blockName))
                    {
                        Error("A block is already declared: " + blockName, blockSpan);
                    }

                    if (required && body.Any(node => node.Kind != SavannaNodeKind.Text || !string.IsNullOrWhiteSpace(node.Value)))
                    {
                        Error("A required block cannot have default content.", blockSpan);
                    }

                    blocks.Add(new SavannaBlock(blockName, required, blockSpan, [.. body]));
                    blockName = null;
                    body      = root;
                    break;
                case "switch":
                    if (value.Length == 0)
                    {
                        Error("A switch needs a C# value expression.", valueSpan);
                    }

                    scopes.Push(("switch", false));
                    body.Add(new SavannaNode(SavannaNodeKind.Switch, value, valueSpan));
                    contentSeen = true;
                    break;
                case "case":
                case "default":
                    if (scopes.Count > 0 && scopes.Peek().Kind == "case")
                    {
                        scopes.Pop();
                    }

                    if (scopes.Count == 0 || scopes.Peek().Kind != "switch")
                    {
                        Error("case and default belong directly inside a switch.", token.Span);
                        break;
                    }

                    if (command == "case" && value.Length == 0 || command == "default" && value.Length != 0)
                    {
                        Error("case requires a value; default takes no arguments.", valueSpan);
                    }

                    // Adjacent labels share a body, even when each label is on its own line.
                    var labelEnd = body.Count;
                    while (labelEnd > 0 && body[labelEnd - 1] is { Kind: SavannaNodeKind.Text } indentation && string.IsNullOrWhiteSpace(indentation.Value))
                    {
                        labelEnd--;
                    }

                    if (labelEnd > 0 && body[labelEnd - 1].Kind is SavannaNodeKind.Case or SavannaNodeKind.Default)
                    {
                        body.RemoveRange(labelEnd, body.Count - labelEnd);
                    }

                    var selection = scopes.Pop();
                    if (command == "default" && selection.HasElse)
                    {
                        Error("A switch may have only one default label.", token.Span);
                    }

                    scopes.Push(("switch", selection.HasElse || command == "default"));
                    scopes.Push(("case", false));
                    body.Add(new SavannaNode(command == "case" ? SavannaNodeKind.Case : SavannaNodeKind.Default, value, valueSpan));
                    break;
                case "endswitch":
                    if (scopes.Count > 0 && scopes.Peek().Kind == "case")
                    {
                        scopes.Pop();
                    }

                    if (scopes.Count == 0 || scopes.Peek().Kind != "switch")
                    {
                        Error("Mismatched closing directive: endswitch", token.Span);
                        break;
                    }

                    if (value.Length != 0)
                    {
                        Error("Closing switch directives do not take arguments.", valueSpan);
                    }

                    scopes.Pop();
                    body.Add(new SavannaNode(SavannaNodeKind.EndSwitch, "", token.Span));
                    break;
                case "break":
                case "continue":
                    // Supplied slots run in separate lambdas, and an empty branch is outside its own loop.
                    var canJump = false;
                    foreach (var scope in scopes)
                    {
                        if (scope.Kind is "include" or "component")
                        {
                            break;
                        }

                        if (scope.Kind is "foreach" or "awaitforeach" or "for" && !scope.HasElse || command == "break" && scope.Kind == "case")
                        {
                            canJump = true;
                            break;
                        }
                    }

                    if (!canJump)
                    {
                        Error(command == "break" ? "break must be inside a switch case or loop." : "continue must be inside a loop.", token.Span);
                    }

                    body.Add(new SavannaNode(command == "break" ? SavannaNodeKind.Break : SavannaNodeKind.Continue, value, valueSpan));
                    contentSeen = true;
                    break;
                case "debug":
                case "release":
                    if (value.Length != 0)
                    {
                        Error("Build directives do not take arguments.", valueSpan);
                    }

                    scopes.Push((command, false));
                    body.Add(new SavannaNode(command == "debug" ? SavannaNodeKind.Debug : SavannaNodeKind.Release, "", token.Span));
                    contentSeen = true;
                    break;
                case "enddebug":
                case "endrelease":
                    if (scopes.Count == 0 || scopes.Peek().Kind != command.Substring(3))
                    {
                        Error("Mismatched closing directive: " + command, token.Span);
                        break;
                    }

                    if (value.Length != 0)
                    {
                        Error("Closing build directives do not take arguments.", valueSpan);
                    }

                    scopes.Pop();
                    body.Add(new SavannaNode(SavannaNodeKind.EndBuild, "", token.Span));
                    break;
                case "if":
                case "unless":
                case "foreach":
                case "for":
                case "await":
                    var control = command;
                    if (command == "await")
                    {
                        if (!value.StartsWith("foreach ", StringComparison.Ordinal) && !value.StartsWith("foreach(", StringComparison.Ordinal))
                        {
                            Error("Only await foreach is supported as an await directive.", valueSpan);
                            break;
                        }

                        control   = "awaitforeach";
                        valueSpan = SavannaLexer.Trim(text.ToString(), TextSpan.FromBounds(valueSpan.Start + 7, valueSpan.End));
                        value     = text.ToString(valueSpan);
                    }

                    if (value.Length == 0)
                    {
                        Error("A control structure needs a C# condition or iteration clause.", valueSpan);
                    }

                    scopes.Push((control, false));
                    body.Add(new SavannaNode(control switch
                    {
                        "if"           => SavannaNodeKind.If,
                        "unless"       => SavannaNodeKind.Unless,
                        "for"          => SavannaNodeKind.For,
                        "awaitforeach" => SavannaNodeKind.AwaitForEach,
                        _              => SavannaNodeKind.ForEach,
                    }, value, valueSpan));
                    contentSeen = true;
                    break;
                case "elseif":
                case "else":
                    if (scopes.Count == 0 || scopes.Peek().Kind is not ("if" or "unless") || scopes.Peek().HasElse)
                    {
                        Error("Unexpected else or elseif.", token.Span);
                        break;
                    }

                    if (command == "else" && value.Length != 0 || command == "elseif" && value.Length == 0)
                    {
                        Error("else has no condition; elseif requires one.", valueSpan);
                    }

                    var condition = scopes.Pop();
                    scopes.Push((condition.Kind, command == "else"));
                    body.Add(new SavannaNode(command == "else" ? SavannaNodeKind.Else : SavannaNodeKind.ElseIf, value, valueSpan));
                    break;
                case "empty":
                    if (scopes.Count == 0 || scopes.Peek().Kind is not ("foreach" or "awaitforeach" or "for") || scopes.Peek().HasElse)
                    {
                        Error("empty belongs to a loop and may appear only once.", token.Span);
                        break;
                    }

                    var loop = scopes.Pop();
                    scopes.Push((loop.Kind, true));
                    body.Add(new SavannaNode(SavannaNodeKind.Empty, "", token.Span));
                    break;
                case "endwhitespace":
                case "endslot":
                case "endinclude":
                case "endcomponent":
                    if (scopes.Count == 0 || scopes.Peek().Kind != command.Substring(3))
                    {
                        Error("Mismatched closing directive: " + command, token.Span);
                        break;
                    }

                    scopes.Pop();
                    body.Add(new SavannaNode(command switch
                    {
                        "endwhitespace" => SavannaNodeKind.EndWhitespace,
                        "endslot"       => SavannaNodeKind.EndSlot,
                        "endinclude"    => SavannaNodeKind.EndInclude,
                        _               => SavannaNodeKind.EndComponent,
                    }, "", token.Span));
                    break;
                case "endif":
                case "endunless":
                case "endforeach":
                case "endfor":
                    var expected = command switch
                    {
                        "endif"     => "if",
                        "endunless" => "unless",
                        "endfor"    => "for",
                        _           => "foreach",
                    };
                    if (scopes.Count == 0 || scopes.Peek().Kind != expected && !(expected == "foreach" && scopes.Peek().Kind == "awaitforeach"))
                    {
                        Error("Mismatched closing directive: " + command, token.Span);
                        break;
                    }

                    if (value.Length != 0)
                    {
                        Error("Closing control directives do not take arguments.", valueSpan);
                    }

                    scopes.Pop();
                    body.Add(new SavannaNode(command switch
                    {
                        "endif" or "endunless" => SavannaNodeKind.EndIf,
                        "endfor"               => SavannaNodeKind.EndFor,
                        _                      => SavannaNodeKind.EndForEach,
                    }, "", token.Span));
                    break;
                case "include":
                case "component":
                    body.Add(ParseCall(document, value, valueSpan, command == "include" ? SavannaNodeKind.Include : SavannaNodeKind.Component, diagnostics));
                    contentSeen = true;
                    break;
                case "html":
                    if (value.Length == 0)
                    {
                        Error("html requires an expression of type SavannaHtml.", valueSpan);
                    }

                    body.Add(new SavannaNode(SavannaNodeKind.Html, value, valueSpan));
                    contentSeen = true;
                    break;
                case "parent":
                    if (blockName is null || document.Parent is null || value.Length != 0)
                    {
                        Error("parent is only valid without arguments inside an overriding block.", token.Span);
                    }

                    body.Add(new SavannaNode(SavannaNodeKind.Parent, "", token.Span));
                    break;
                default:
                    Error("Unknown Savanna directive: " + command, token.Span);
                    break;
            }
        }

        if (!modelSeen && !document.IsImports)
        {
            Error("A template must declare its model with <#model type=\"Type\" />.", default);
        }

        if (scopes.Count > 0 || blockName is not null)
        {
            Error("A block or control structure is not closed.", new TextSpan(text.Length, 0));
        }

        if (document.Parent is not null && root.Any(node => node.Kind != SavannaNodeKind.Block && (node.Kind != SavannaNodeKind.Text || !string.IsNullOrWhiteSpace(node.Value))))
        {
            Error("An extending template may render content only inside named blocks.", document.Parent.Span);
        }

        document.Body    = [.. root];
        document.Blocks  = [.. blocks];
        document.Imports = imports;
        document.Usings  = [.. usings.Distinct(StringComparer.Ordinal)];
        if (document.IsImports && (modelSeen || document.IsLayout || document.Parent is not null || blocks.Count > 0 || imports.Count > 0 || contentSeen))
        {
            Error("An _Imports.svn file may contain only namespace imports and comments.", default);
        }

        if (diagnostics.Count == 0)
        {
            // Slot analysis assumes paired constructs are balanced. Avoid follow-on errors from broken syntax.
            SavannaSlotsAnalysis.Analyze(document, diagnostics);
        }

        document.Diagnostics = [.. diagnostics];
        return document;
    }

    internal static bool TryPath(string text, out string path)
    {
        var expression = SyntaxFactory.ParseExpression(text);
        if (expression is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression) && !expression.ContainsDiagnostics)
        {
            path = literal.Token.ValueText;
            return path.Length > 0 && path.IndexOf('\\') < 0 && !path.StartsWith("/", StringComparison.Ordinal)
                   && path.Split('/').All(part => part.Length > 0 && part is not ("." or ".."));
        }

        path = "";
        return false;
    }

    private static SavannaNode ParseCall(SavannaDocument document, string text, TextSpan span, SavannaNodeKind kind, List<SavannaDiagnostic> diagnostics)
    {
        var targetEnd = kind == SavannaNodeKind.Component
            ? SyntaxFactory.ParseTypeName(text, consumeFullText: false).Span.End
            : SyntaxFactory.ParseTokens(text).FirstOrDefault().Span.End;
        var with = SyntaxFactory.ParseTokens(text, targetEnd, targetEnd).FirstOrDefault();

        if (with.Text != "with")
        {
            diagnostics.Add(new SavannaDiagnostic("SAVANNA001", "Pass an explicit model or component argument with 'with'.", document.Path, span));
            return new SavannaNode(kind, text, span);
        }

        var target       = text.Substring(0, with.SpanStart).Trim();
        var argumentSpan = SavannaLexer.Trim(document.Text.ToString(), TextSpan.FromBounds(span.Start + with.Span.End, span.End));
        var argument     = document.Text.ToString(argumentSpan);
        if (target.Length == 0 || argument.Length == 0)
        {
            diagnostics.Add(new SavannaDiagnostic("SAVANNA001", "Both a target and an argument are required.", document.Path, span));
        }

        return new SavannaNode(kind, target, new TextSpan(span.Start, target.Length), argument, argumentSpan);
    }
}
