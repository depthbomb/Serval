#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.CodeAnalysis.Text;
using System.Text;
using Serval.Savanna.Compiler;
using Serval.Savanna.Compiler.Parsing;
using Serval.Savanna.Compiler.Syntax;
using Serval.Savanna.LanguageServer.Rpc;

namespace Serval.Savanna.LanguageServer.Features;

public static class TemplateFormatter
{
    // Formatting directive headers never rewrites rendered HTML or C# expressions.
    public static object[] Format(SavannaDocument document)
    {
        var diagnostics = new List<SavannaDiagnostic>();
        var tokens      = SavannaLexer.Tokenize(document, diagnostics, CancellationToken.None);
        if (diagnostics.Count > 0)
        {
            return [];
        }

        var edits = new List<object>();
        foreach (var token in tokens)
        {
            if (token.Tag is not {} tag)
            {
                continue;
            }

            var text = new StringBuilder(tag.Closing ? "</#" : "<#").Append(tag.Name);
            foreach (var attribute in tag.Attributes.Values.OrderBy(attribute => attribute.NameSpan.Start))
            {
                text.Append(' ').Append(attribute.Name);
                var quoteOffset = attribute.Span.Start - 1;
                if (quoteOffset >= 0 && document.Text[quoteOffset] is '\'' or '"')
                {
                    text.Append('=').Append(document.Text.ToString(new TextSpan(quoteOffset, attribute.Span.Length + 2)));
                }
            }

            text.Append(tag.SelfClosing && !SavannaTagRules.IsStandalone(tag.Name) ? " />" : ">");
            var formatted = text.ToString();
            if (formatted != document.Text.ToString(tag.Span))
            {
                edits.Add(new
                {
                    range   = Position.Range(document.Text, tag.Span),
                    newText = formatted,
                });
            }
        }

        return [.. edits];
    }
}
