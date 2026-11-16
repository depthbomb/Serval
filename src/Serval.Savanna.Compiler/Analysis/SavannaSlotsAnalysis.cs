#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Serval.Savanna.Compiler.Syntax;

namespace Serval.Savanna.Compiler.Analysis;

internal static class SavannaSlotsAnalysis
{
    public static HashSet<SavannaNode> RenderedNodes(IReadOnlyList<SavannaDocument> chain)
    {
        var result = new HashSet<SavannaNode>();
        if (chain.Count > 0)
        {
            Add(chain[chain.Count - 1].Body, chain.Count - 1, null);
        }

        return result;

        void Add(IReadOnlyList<SavannaNode> nodes, int owner, string? blockName)
        {
            foreach (var node in nodes)
            {
                if (!result.Add(node))
                {
                    continue;
                }

                if (node.Kind is not (SavannaNodeKind.Block or SavannaNodeKind.Parent))
                {
                    continue;
                }

                var name = node.Kind == SavannaNodeKind.Block ? node.Value : blockName;
                // Ordinary blocks choose the most-derived body; parent starts beyond the current owner.
                for (var index = node.Kind == SavannaNodeKind.Block ? 0 : owner + 1; index < chain.Count; index++)
                {
                    var block = chain[index].Blocks.FirstOrDefault(candidate => candidate.Name == name && !candidate.Required);
                    if (block is null)
                    {
                        continue;
                    }

                    Add(block.Body, index, name);
                    break;
                }
            }
        }
    }

    public static (IReadOnlyList<SavannaNode> Declarations, IReadOnlyList<Call> Calls) Analyze(SavannaDocument document, List<SavannaDiagnostic> diagnostics)
    {
        var declarations = new List<SavannaNode>();
        var calls        = new List<Call>();
        foreach (var body in new[] { document.Body }.Concat(document.Blocks.Select(block => block.Body)))
        {
            var stack      = new Stack<Call>();
            // A slot inside a call supplies content. A slot outside one declares a place to render content.
            var slotOwners = new Stack<Call?>();
            foreach (var node in body)
            {
                var owner = stack.Count > 0 ? stack.Peek() : null;
                if (owner is { InSlot: false } && node.Kind is not (SavannaNodeKind.Slot or SavannaNodeKind.EndInclude or SavannaNodeKind.EndComponent)
                                               && (node.Kind != SavannaNodeKind.Text || !string.IsNullOrWhiteSpace(node.Value)))
                {
                    Error(node, "A paired include or component may contain only named slot definitions.");
                }

                if (node.Kind is SavannaNodeKind.Include or SavannaNodeKind.Component)
                {
                    var call = new Call(node);
                    calls.Add(call);
                    if (node.Paired)
                    {
                        stack.Push(call);
                    }
                }
                else if (node.Kind is SavannaNodeKind.EndInclude or SavannaNodeKind.EndComponent)
                {
                    if (stack.Count > 0)
                    {
                        stack.Pop();
                    }
                }
                else if (node.Kind == SavannaNodeKind.Slot)
                {
                    if (owner is { InSlot: false })
                    {
                        if (!node.Paired || node.Required)
                        {
                            Error(node, "A supplied slot needs a body and cannot be marked required.");
                        }

                        if (!owner.Supplied.Add(node.Value))
                        {
                            Error(node, "A slot is supplied more than once: " + node.Value);
                        }

                        owner.InSlot = node.Paired;
                        if (node.Paired)
                        {
                            slotOwners.Push(owner);
                        }
                    }
                    else
                    {
                        if (node is { Required: true, Paired: true })
                        {
                            Error(node, "A required slot cannot have fallback content. Use a self-closing tag.");
                        }

                        declarations.Add(node);
                        if (node.Paired)
                        {
                            slotOwners.Push(null);
                        }
                    }
                }
                else if (node.Kind == SavannaNodeKind.EndSlot && slotOwners.Count > 0)
                {
                    var slotOwner = slotOwners.Pop();
                    slotOwner?.InSlot = false;
                }
            }
        }

        return (declarations, calls);

        void Error(SavannaNode node, string message)
        {
            diagnostics.Add(new SavannaDiagnostic("SAVANNA001", message, document.Path, node.Span));
        }
    }

    internal sealed class Call
    {
        public SavannaNode        Node     { get; }
        public HashSet<string> Supplied { get; }
        public bool            InSlot   { get; set; }

        public Call(SavannaNode node)
        {
            Node     = node;
            Supplied = new HashSet<string>(StringComparer.Ordinal);
        }
    }
}
