#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Serval.Savanna.Compiler.Syntax;

namespace Serval.Savanna.Compiler.Html;

// A branch or jump must not change how the browser reads the output that follows it.
internal sealed class SavannaHtmlControlFlow
{
    private readonly Stack<(SavannaNodeKind Kind, string? Container, bool Empty)> _scopes = new();

    public void Visit(SavannaDocument document, SavannaNode node, SavannaHtmlContext html, List<SavannaDiagnostic> diagnostics)
    {
        switch (node.Kind)
        {
            case SavannaNodeKind.If:
            case SavannaNodeKind.Unless:
            case SavannaNodeKind.Debug:
            case SavannaNodeKind.Release:
            case SavannaNodeKind.Switch:
            case SavannaNodeKind.For:
            case SavannaNodeKind.ForEach:
            case SavannaNodeKind.AwaitForEach:
            case SavannaNodeKind.Slot when node.Paired:
                _scopes.Push((node.Kind, html.TextContainer, false));
                break;
            case SavannaNodeKind.Else:
            case SavannaNodeKind.ElseIf:
            case SavannaNodeKind.Case:
            case SavannaNodeKind.Default:
            case SavannaNodeKind.Empty:
                if (_scopes.Count > 0)
                {
                    var scope = _scopes.Pop();
                    Check(document, node, html, scope.Container, diagnostics);
                    _scopes.Push((scope.Kind, scope.Container, scope.Empty || node.Kind == SavannaNodeKind.Empty));
                }

                break;
            case SavannaNodeKind.EndIf:
            case SavannaNodeKind.EndBuild:
            case SavannaNodeKind.EndSwitch:
            case SavannaNodeKind.EndFor:
            case SavannaNodeKind.EndForEach:
            case SavannaNodeKind.EndSlot:
                if (_scopes.Count > 0)
                {
                    Check(document, node, html, _scopes.Pop().Container, diagnostics);
                }

                break;
            case SavannaNodeKind.Break:
            case SavannaNodeKind.Continue:
                foreach (var scope in _scopes)
                {
                    if (scope.Kind is SavannaNodeKind.For or SavannaNodeKind.ForEach or SavannaNodeKind.AwaitForEach && !scope.Empty || node.Kind == SavannaNodeKind.Break && scope.Kind == SavannaNodeKind.Switch)
                    {
                        Check(document, node, html, scope.Container, diagnostics);
                        break;
                    }
                }

                break;
        }
    }

    private static void Check(SavannaDocument document, SavannaNode node, SavannaHtmlContext html, string? container, List<SavannaDiagnostic> diagnostics)
    {
        if (container == html.TextContainer)
        {
            return;
        }

        const string message = "A branch or jump must stay in the same HTML text context. Open and close title or textarea within the same branch.";
        foreach (var diagnostic in diagnostics)
        {
            if (diagnostic.Path == document.Path && diagnostic.Span == node.Span && diagnostic.Message == message)
            {
                return;
            }
        }

        diagnostics.Add(new SavannaDiagnostic("SAVANNA003", message, document.Path, node.Span));
    }
}
