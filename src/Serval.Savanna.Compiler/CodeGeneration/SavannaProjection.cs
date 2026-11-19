#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using System.Text;
using Serval.Savanna.Compiler.Parsing;
using Serval.Savanna.Compiler.Syntax;

namespace Serval.Savanna.Compiler.CodeGeneration;

// Design-time code keeps each expression in its original lexical scope. It is never executed.
/// <summary>Creates non-executable C# projections for editor analysis.</summary>
public static class SavannaProjection
{
    private static readonly string[] DefaultUsings = ["System", "System.Linq", "System.Collections.Generic", "System.Threading.Tasks", "Serval.Savanna"];

    /// <summary>Projects template expressions into C# while preserving lexical scopes and source mappings.</summary>
    /// <param name="document">
    ///     The parsed document; apply inherited imports first when project context is available.
    /// </param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The editor-only source and mappings back to template expressions.</returns>
    /// <remarks>
    ///     Tolerates incomplete syntax for completion and diagnostics. The projection is not a runtime render
    ///     implementation.
    /// </remarks>
    public static SavannaGeneratedSource Create(SavannaDocument document, CancellationToken cancellationToken = default)
    {
        var code     = new StringBuilder("#nullable enable\n");
        var mappings = new List<SavannaSourceMapping>();
        var tokens   = SavannaLexer.Tokenize(document, [], cancellationToken, true);
        var usings   = new HashSet<string>(StringComparer.Ordinal);

        // Keep explicit declarations mapped, then add only missing inherited and default imports.
        foreach (var token in tokens.Where(token => IsDeclaration(token, "using")))
        {
            var span = Argument(token, 5);
            usings.Add(document.Text.ToString(span));
            code.Append("using ");
            Copy(span);
            code.AppendLine(";");
        }

        foreach (var name in document.Usings.Concat(DefaultUsings))
        {
            if (usings.Add(name))
            {
                code.Append("using ").Append(name).AppendLine(";");
            }
        }

        code.AppendLine("internal class __SavannaEditorProjection {");
        code.Append("private ");
        var model = tokens.FirstOrDefault(token => IsDeclaration(token, "model"));
        if (model is not null)
        {
            Copy(Argument(model, 5));
        }
        else
        {
            code.Append("object");
        }

        code.AppendLine(" Model = default!;");
        code.AppendLine("private SavannaRenderContext Context = default!;");
        code.AppendLine("private global::Serval.Routing.IUrlGenerator Urls = default!;");
        code.AppendLine("private static void __Value<T>(T value) {}\nprivate static void __Html(SavannaHtml value) {}");
        code.AppendLine("private async Task __Render() {");
        var scopes = new Stack<string>();
        foreach (var token in tokens)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (token.Kind == SavannaTokenKind.Expression)
            {
                code.Append("__Value(");
                Copy(token.Span);
                code.AppendLine(");");
                continue;
            }

            if (token.Kind != SavannaTokenKind.Directive)
            {
                continue;
            }

            var split    = token.Value.IndexOfAny([' ', '\t', '\r', '\n']);
            var command  = split < 0 ? token.Value : token.Value.Substring(0, split);
            var argument = Argument(token, command.Length);
            if (token.Tag is { Closing: false } tag)
            {
                switch (command)
                {
                    case "foreach":
                    {
                        code.Append(tag.Get("await") is null ? "foreach (var " : "await foreach (var ");
                        CopyAttribute(tag, "item");
                        code.Append(" in ");
                        CopyAttribute(tag, "in");
                        code.AppendLine(") {");
                        scopes.Push("foreach");
                        continue;
                    }

                    case "let":
                    {
                        code.Append("var ");
                        CopyAttribute(tag, "name");
                        code.Append(" = (");
                        CopyAttribute(tag, "value");
                        code.AppendLine(");");
                        continue;
                    }

                    case "extends" or "include" or "component" or "json":
                    {
                        if (command == "component")
                        {
                            code.Append("_ = typeof(");
                            CopyAttribute(tag, "type");
                            code.AppendLine(");");
                        }

                        var expression = tag.Get(command switch
                        {
                            "component" => "arguments",
                            "json"      => "value",
                            _           => "model",
                        });
                        if (expression is not null)
                        {
                            code.Append("__Value(");
                            Copy(expression.Span);
                            code.AppendLine(");");
                        }

                        if (!tag.SelfClosing && command is "include" or "component")
                        {
                            code.AppendLine("{");
                            scopes.Push(command);
                        }

                        continue;
                    }

                    case "slot" or "whitespace":
                    {
                        if (!tag.SelfClosing)
                        {
                            code.AppendLine("{");
                            scopes.Push(command);
                        }

                        continue;
                    }
                }
            }

            switch (command)
            {
                case "debug":
                case "release":
                    code.AppendLine(command == "debug" ? "#if DEBUG" : "#if !DEBUG").AppendLine("{");
                    scopes.Push(command);
                    break;
                case "unless":
                    code.Append("if (!(");
                    Copy(argument);
                    code.AppendLine(")) {");
                    scopes.Push(command);
                    break;
                case "switch":
                    code.Append("switch (");
                    Copy(argument);
                    code.AppendLine(") {");
                    scopes.Push(command);
                    break;
                case "case":
                    code.Append("case ");
                    Copy(argument);
                    code.AppendLine(":");
                    break;
                case "default":
                    code.AppendLine("default:");
                    break;
                case "break":
                case "continue":
                    var conditionalJump = argument.Length > 0 || token.Tag?.Get("if") is not null;
                    if (conditionalJump)
                    {
                        code.Append("if (");
                        Copy(argument);
                        code.AppendLine(") {");
                    }

                    code.Append(command).AppendLine(";");
                    if (conditionalJump)
                    {
                        code.AppendLine("}");
                    }

                    break;
                case "if":
                case "foreach":
                case "for":
                case "await":
                    code.Append(command == "await" ? "await foreach" : command).Append(' ');
                    if (command == "await" && document.Text.ToString(argument).StartsWith("foreach", StringComparison.Ordinal))
                    {
                        argument = SavannaLexer.Trim(document.Text.ToString(), TextSpan.FromBounds(argument.Start + 7, argument.End));
                    }

                    var hasParentheses = document.Text.ToString(argument).StartsWith("(", StringComparison.Ordinal);
                    if (!hasParentheses)
                    {
                        code.Append('(');
                    }

                    Copy(argument);
                    if (!hasParentheses)
                    {
                        code.Append(')');
                    }

                    code.AppendLine(" {");
                    scopes.Push(command);
                    break;
                case "elseif":
                    if (scopes.Count > 0 && scopes.Peek() is "if" or "unless")
                    {
                        code.Append("} else if (");
                        Copy(argument);
                        code.AppendLine(") {");
                    }

                    break;
                case "else":
                    if (scopes.Count > 0 && scopes.Peek() is "if" or "unless")
                    {
                        code.AppendLine("} else {");
                    }

                    break;
                case "empty":
                    if (scopes.Count > 0 && scopes.Peek() is "foreach" or "await" or "for")
                    {
                        code.AppendLine("} if (true) {");
                    }

                    break;
                case "endif":
                case "endunless":
                case "enddebug":
                case "endrelease":
                case "endswitch":
                case "endforeach":
                case "endfor":
                case "endblock":
                case "endslot":
                case "endinclude":
                case "endcomponent":
                case "endwhitespace":
                    if (scopes.Count > 0)
                    {
                        CloseScope(scopes.Pop());
                    }

                    break;
                case "block":
                    code.AppendLine("{");
                    scopes.Push("block");
                    break;
                case "html":
                    code.Append("__Html(");
                    Copy(argument);
                    code.AppendLine(");");
                    break;
                case "extends":
                case "include":
                case "component":
                    var value = document.Text.ToString(argument);
                    var targetEnd = command == "component"
                        ? SyntaxFactory.ParseTypeName(value, consumeFullText: false).Span.End
                        : SyntaxFactory.ParseTokens(value).FirstOrDefault().Span.End;
                    var with = SyntaxFactory.ParseTokens(value, targetEnd, targetEnd).FirstOrDefault();
                    if (command == "component" && targetEnd > 0)
                    {
                        code.Append("_ = typeof(");
                        Copy(new TextSpan(argument.Start, targetEnd));
                        code.AppendLine(");");
                    }

                    if (with.Text == "with")
                    {
                        code.Append("__Value(");
                        Copy(SavannaLexer.Trim(document.Text.ToString(), TextSpan.FromBounds(argument.Start + with.Span.End, argument.End)));
                        code.AppendLine(");");
                    }

                    break;
            }
        }

        foreach (var scope in scopes)
        {
            CloseScope(scope);
        }

        code.AppendLine("await Task.CompletedTask;\n}\n}");
        return new SavannaGeneratedSource("__SavannaEditorProjection.cs", code.ToString(), [.. mappings]);

        void CloseScope(string scope)
        {
            code.AppendLine("}");
            if (scope is "debug" or "release")
            {
                code.AppendLine("#endif");
            }
        }

        TextSpan Argument(SavannaToken token, int commandLength)
        {
            return token.Tag is {} tag
                ? tag.Get(SavannaTagRules.Primary(tag.Name) ?? "")?.Span ?? new TextSpan(tag.Span.End, 0)
                : SavannaLexer.Trim(document.Text.ToString(), TextSpan.FromBounds(token.Span.Start + commandLength, token.Span.End));
        }

        void CopyAttribute(SavannaTag tag, string name)
        {
            if (tag.Get(name) is {} attribute)
            {
                Copy(attribute.Span);
            }
        }

        void Copy(TextSpan span)
        {
            mappings.Add(new SavannaSourceMapping(document.Path, span, new TextSpan(code.Length, span.Length)));
            code.Append(document.Text.ToString(span));
            code.AppendLine();
        }
    }

    private static bool IsDeclaration(SavannaToken token, string keyword)
        => token.Kind == SavannaTokenKind.Directive && (token.Tag is { Closing: false } tag ? tag.Name == keyword : token.Value.Length > keyword.Length && token.Value.StartsWith(keyword, StringComparison.Ordinal) && char.IsWhiteSpace(token.Value[keyword.Length]));
}
