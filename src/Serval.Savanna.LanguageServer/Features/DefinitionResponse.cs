#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using System.Text.Json;
using Serval.Savanna.Compiler.Parsing;
using Serval.Savanna.Compiler.Syntax;
using Serval.Savanna.LanguageServer.Rpc;

namespace Serval.Savanna.LanguageServer.Features;

internal static class DefinitionResponse
{
    public static object[] Create(object? target, SavannaDocument document, int offset, bool linkSupport)
    {
        if (target is null)
        {
            return [];
        }

        if (!linkSupport)
        {
            return [target];
        }

        // Give Rider the source token range so Ctrl+hover can underline and cache the whole reference.
        var origin = new TextSpan(offset, 0);
        var token = SavannaLexer.Tokenize(document, [], CancellationToken.None, true)
                                .FirstOrDefault(token => token.Kind != SavannaTokenKind.Text && token.Span.Start <= offset && token.Span.End >= offset);
        if (token is not null)
        {
            var sourceSpan = token.Tag is {} tag
                ? tag.Attributes.Values.FirstOrDefault(attribute => attribute.Span.Start <= offset && offset <= attribute.Span.End)?.Span
                  ?? new TextSpan(tag.Span.Start + (tag.Closing ? 3 : 2), tag.Name.Length)
                : token.Span;
            var parts    = SyntaxFactory.ParseTokens(document.Text.ToString(sourceSpan)).ToArray();
            var relative = offset - sourceSpan.Start;
            var part     = parts.FirstOrDefault(part => part.Span.Contains(relative));
            if (part.Span.Length == 0 || part.SpanStart == relative && !part.IsKind(SyntaxKind.IdentifierToken))
            {
                var previous = parts.FirstOrDefault(candidate => candidate.Span.End == relative && candidate.IsKind(SyntaxKind.IdentifierToken));
                if (previous.Span.Length > 0)
                {
                    part = previous;
                }
            }

            if (part.Span.Length > 0)
            {
                origin = new TextSpan(sourceSpan.Start + part.Span.Start, part.Span.Length);
            }
        }

        var location = JsonSerializer.SerializeToElement(target);
        return
        [
            new
            {
                originSelectionRange = Position.Range(document.Text, origin),
                targetUri            = location.GetProperty("uri"),
                targetRange          = location.GetProperty("range"),
                targetSelectionRange = location.GetProperty("range"),
            },
        ];
    }
}
