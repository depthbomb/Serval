#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Serval.Savanna.Compiler.Parsing;
using Serval.Savanna.Compiler.Syntax;

namespace Serval.Savanna.LanguageServer.Features;

public readonly record struct SemanticToken(TextSpan Span, string Type);

public static class SemanticTokens
{
    public static IReadOnlyList<string> Types { get; } = ["namespace", "type", "class", "enum", "interface", "struct", "typeParameter", "parameter", "variable", "property", "enumMember", "event", "method", "keyword", "comment", "string", "number", "operator"];

    public static object Encode(SavannaDocument document, IEnumerable<SemanticToken> csharp)
    {
        var tokens = new List<SemanticToken>(csharp);
        foreach (var token in SavannaLexer.Tokenize(document, [], CancellationToken.None, true).Where(token => token.Kind == SavannaTokenKind.Directive))
        {
            if (token.Tag is { } tag)
            {
                tokens.Add(new SemanticToken(new TextSpan(tag.Span.Start + (tag.Closing ? 3 : 2), tag.Name.Length), "keyword"));
                foreach (var attribute in tag.Attributes.Values)
                {
                    tokens.Add(new SemanticToken(attribute.NameSpan, attribute.Name is "required" or "await" ? "keyword" : "property"));
                    if (attribute.Name is "view" or "as" or "id" or "mode" || attribute.Name == "name" && tag.Name is "slot" or "block")
                    {
                        tokens.Add(new SemanticToken(attribute.Span, "string"));
                    }
                }

                continue;
            }

            var commandLength = token.Value.TakeWhile(character => !char.IsWhiteSpace(character)).Count();
            tokens.Add(new SemanticToken(new TextSpan(token.Span.Start, commandLength), "keyword"));
            var command = token.Value[..commandLength];
            if (command is not ("import" or "include" or "extends" or "block"))
            {
                continue;
            }

            foreach (var part in SyntaxFactory.ParseTokens(token.Value, commandLength, token.Span.Start + commandLength))
            {
                if (tokens.Any(existing => existing.Span.OverlapsWith(part.Span)))
                {
                    continue;
                }

                var type = part.IsKind(SyntaxKind.StringLiteralToken) ? "string"
                    : part.Text is "with" or "as" or "required"       ? "keyword"
                    : part.IsKind(SyntaxKind.IdentifierToken)         ? "variable" : null;
                if (type is not null)
                {
                    tokens.Add(new SemanticToken(part.Span, type));
                }
            }
        }

        var data              = new List<int>();
        var previousLine      = 0;
        var previousCharacter = 0;
        var previousEnd       = 0;
        foreach (var token in tokens.OrderBy(token => token.Span.Start).ThenByDescending(token => token.Span.Length))
        {
            if (token.Span.Length == 0 || token.Span.Start < previousEnd || token.Span.End > document.Text.Length)
            {
                continue;
            }

            var type = Types.IndexOf(token.Type);
            if (type < 0)
            {
                continue;
            }

            var position = token.Span.Start;
            while (position < token.Span.End)
            {
                var line      = document.Text.Lines.GetLineFromPosition(position);
                var end       = Math.Min(token.Span.End, line.End);
                var character = position - line.Start;
                if (end > position)
                {
                    data.AddRange([line.LineNumber - previousLine, line.LineNumber == previousLine ? character - previousCharacter : character, end - position, type, 0]);
                    previousLine      = line.LineNumber;
                    previousCharacter = character;
                }

                position = Math.Min(token.Span.End, line.EndIncludingLineBreak);
            }

            previousEnd = token.Span.End;
        }

        return new { data };
    }

    private static int IndexOf(this IReadOnlyList<string> values, string value)
    {
        for (var index = 0; index < values.Count; index++)
        {
            if (values[index] == value)
            {
                return index;
            }
        }

        return -1;
    }
}
