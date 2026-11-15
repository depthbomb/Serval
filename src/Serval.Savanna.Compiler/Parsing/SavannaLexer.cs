#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Serval.Savanna.Compiler.Syntax;

namespace Serval.Savanna.Compiler.Parsing;

/// <summary>Distinguishes literal markup, interpolation, and Savanna directives.</summary>
public enum SavannaTokenKind
{
    /// <summary>Literal text that will be emitted as markup.</summary>
    Text,

    /// <summary>An embedded C# value expression.</summary>
    Expression,

    /// <summary>A Savanna control, declaration, or rendering directive.</summary>
    Directive,
}

/// <summary>Represents one token with its original source coordinates.</summary>
public sealed class SavannaToken
{
    /// <summary>The token category.</summary>
    public SavannaTokenKind Kind { get; }

    /// <summary>The token text or normalized directive representation.</summary>
    public string Value { get; }

    /// <summary>
    ///     The UTF-16 source range. For expressions it covers the expression content rather than its delimiters.
    /// </summary>
    public TextSpan Span { get; }

    /// <summary>The parsed Savanna tag for tag-style directives, or null for other syntax.</summary>
    public SavannaTag? Tag { get; internal set; }

    /// <summary>Creates a token with its text and source range.</summary>
    /// <param name="kind">The token category.</param>
    /// <param name="value">The literal text, C# expression, or normalized directive text.</param>
    /// <param name="span">The source span used by this token.</param>
    public SavannaToken(SavannaTokenKind kind, string value, TextSpan span)
    {
        Kind  = kind;
        Value = value;
        Span  = span;
    }
}

/// <summary>Tokenizes tag-style and legacy Savanna syntax while respecting C# strings and comments.</summary>
public static class SavannaLexer
{
    /// <summary>Reads tokens and appends lexical diagnostics to the supplied list.</summary>
    /// <param name="document">The document and source text to tokenize.</param>
    /// <param name="diagnostics">The caller-owned list to which lexical failures are appended.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <param name="tolerateIncomplete">
    ///     Whether to preserve incomplete expressions for editor projection; defaults to false.
    /// </param>
    /// <returns>The ordered tokens with source spans.</returns>
    /// <remarks>
    ///     Savanna comments are removed. Verbatim blocks become literal text without interpreting embedded Savanna markers.
    /// </remarks>
    public static IReadOnlyList<SavannaToken> Tokenize(SavannaDocument         document,
                                                    List<SavannaDiagnostic> diagnostics,
                                                    CancellationToken    cancellationToken,
                                                    bool                 tolerateIncomplete = false)
    {
        var text           = document.Text.ToString();
        var tokens         = new List<SavannaToken>();
        var offset         = 0;
        var nextBrace      = -1;
        var nextTag        = -1;
        var nextClosingTag = -1;
        while (offset < text.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (nextBrace < offset)
            {
                nextBrace = text.IndexOf('{', offset);
                while (nextBrace >= 0 && (nextBrace + 1 >= text.Length || text[nextBrace + 1] is not ('{' or '%' or '#')))
                {
                    nextBrace = text.IndexOf('{', nextBrace + 1);
                }

                if (nextBrace < 0)
                {
                    nextBrace = text.Length;
                }
            }

            if (nextTag < offset)
            {
                nextTag = text.IndexOf("<#", offset, StringComparison.Ordinal);
                if (nextTag < 0)
                {
                    nextTag = text.Length;
                }
            }

            if (nextClosingTag < offset)
            {
                nextClosingTag = text.IndexOf("</#", offset, StringComparison.Ordinal);
                if (nextClosingTag < 0)
                {
                    nextClosingTag = text.Length;
                }
            }

            var opening  = nextBrace == text.Length ? -1 : nextBrace;
            var tagStart = Math.Min(nextTag, nextClosingTag);
            if (tagStart == text.Length)
            {
                tagStart = -1;
            }

            if (tagStart >= 0 && (opening < 0 || tagStart < opening))
            {
                if (tagStart > offset)
                {
                    tokens.Add(new SavannaToken(SavannaTokenKind.Text, text.Substring(offset, tagStart - offset), TextSpan.FromBounds(offset, tagStart)));
                }

                if (tagStart + 4 <= text.Length && string.CompareOrdinal(text, tagStart, "<#--", 0, 4) == 0)
                {
                    var end = text.IndexOf("-->", tagStart + 4, StringComparison.Ordinal);
                    if (end < 0)
                    {
                        diagnostics.Add(new SavannaDiagnostic("SAVANNA001", "Unterminated Savanna comment.", document.Path, TextSpan.FromBounds(tagStart, text.Length)));
                        break;
                    }

                    offset = end + 3;
                    continue;
                }

                var tag = SavannaTag.Read(text, tagStart, document.Path, diagnostics);
                offset = tag.Span.End;
                if (tag is { Name: "verbatim", Closing: false, SelfClosing: false, Attributes.Count: 0 })
                {
                    const string ending = "</#verbatim>";

                    var end = text.IndexOf(ending, offset, StringComparison.Ordinal);
                    if (end < 0)
                    {
                        diagnostics.Add(new SavannaDiagnostic("SAVANNA001", "Missing </#verbatim>.", document.Path, tag.Span));
                        end = text.Length;
                    }

                    tokens.Add(new SavannaToken(SavannaTokenKind.Text, text.Substring(offset, end - offset), TextSpan.FromBounds(offset, end)));
                    offset = Math.Min(end + ending.Length, text.Length);
                    continue;
                }

                tokens.Add(new SavannaToken(SavannaTokenKind.Directive, (tag.Closing ? "end" : "") + tag.Name, tag.Span) { Tag = tag });
                continue;
            }

            if (opening < 0)
            {
                tokens.Add(new SavannaToken(SavannaTokenKind.Text, text.Substring(offset), TextSpan.FromBounds(offset, text.Length)));
                break;
            }

            if (opening > offset)
            {
                tokens.Add(new SavannaToken(SavannaTokenKind.Text, text.Substring(offset, opening - offset), TextSpan.FromBounds(offset, opening)));
            }

            var marker = text[opening + 1];
            var start  = opening + 2;
            var close  = marker == '#' ? text.IndexOf("#}", start, StringComparison.Ordinal) : FindEnd(text, start, marker, cancellationToken);
            if (close < 0)
            {
                diagnostics.Add(new SavannaDiagnostic("SAVANNA001", "Unterminated template expression, directive, or comment.", document.Path, TextSpan.FromBounds(opening, text.Length)));
                if (tolerateIncomplete && marker != '#')
                {
                    var incompleteSpan = Trim(text, TextSpan.FromBounds(start, text.Length));
                    tokens.Add(new SavannaToken(marker == '{' ? SavannaTokenKind.Expression : SavannaTokenKind.Directive, text.Substring(incompleteSpan.Start), new TextSpan(incompleteSpan.Start, text.Length - incompleteSpan.Start)));
                }

                break;
            }

            offset = close + 2;
            if (marker == '#')
            {
                continue;
            }

            var span  = Trim(text, TextSpan.FromBounds(start, close));
            var value = text.Substring(span.Start, span.Length);
            if (marker == '%' && value == "verbatim")
            {
                const string ending = "{% endverbatim %}";
                var          end    = text.IndexOf(ending, offset, StringComparison.Ordinal);
                if (end < 0)
                {
                    diagnostics.Add(new SavannaDiagnostic("SAVANNA001", "Missing {% endverbatim %}.", document.Path, span));
                    break;
                }

                tokens.Add(new SavannaToken(SavannaTokenKind.Text, text.Substring(offset, end - offset), TextSpan.FromBounds(offset, end)));
                offset = end + ending.Length;
                continue;
            }

            tokens.Add(new SavannaToken(marker == '{' ? SavannaTokenKind.Expression : SavannaTokenKind.Directive, value, span));
        }

        return tokens;
    }

    /// <summary>Removes surrounding whitespace from a source range.</summary>
    /// <param name="text">The full source text.</param>
    /// <param name="span">A range contained within the text.</param>
    /// <returns>The trimmed range in the original coordinate system.</returns>
    public static TextSpan Trim(string text, TextSpan span)
    {
        var start = span.Start;
        var end   = span.End;
        while (start < end && char.IsWhiteSpace(text[start]))
        {
            start++;
        }

        while (end > start && char.IsWhiteSpace(text[end - 1]))
        {
            end--;
        }

        return TextSpan.FromBounds(start, end);
    }

    private static int FindEnd(string text, int start, char marker, CancellationToken cancellationToken)
    {
        var braces      = 0;
        var parentheses = 0;
        var brackets    = 0;
        // Roslyn owns C# string/comment lexing, including interpolated and raw strings.
        foreach (var token in SyntaxFactory.ParseTokens(text, start, start, CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var position = token.SpanStart;
            if (braces            == 0                           && parentheses        == 0 && brackets == 0 && position + 1 < text.Length
                && text[position] == (marker == '{' ? '}' : '%') && text[position + 1] == '}')
            {
                return position;
            }

            switch (token.Kind())
            {
                case SyntaxKind.OpenBraceToken:    braces++; break;
                case SyntaxKind.CloseBraceToken:   braces--; break;
                case SyntaxKind.OpenParenToken:    parentheses++; break;
                case SyntaxKind.CloseParenToken:   parentheses--; break;
                case SyntaxKind.OpenBracketToken:  brackets++; break;
                case SyntaxKind.CloseBracketToken: brackets--; break;
            }

            if (braces < 0 || parentheses < 0 || brackets < 0)
            {
                return -1;
            }
        }

        return -1;
    }
}
