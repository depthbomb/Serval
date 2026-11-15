#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Security.Cryptography;
using System.Text;
using Serval.Savanna.Compiler.Syntax;

namespace Serval.Savanna.Compiler.CodeGeneration;

// Comments are template literals. Build their text here so rendering never needs metadata or hashing.
internal static class SavannaComments
{
    public static (string Brief, string Detailed) Template(string kind, IReadOnlyList<SavannaDocument> sources)
    {
        var brief    = new StringBuilder();
        var detailed = new StringBuilder();
        for (var index = 0; index < sources.Count; index++)
        {
            var source     = sources[index];
            var partials   = 0;
            var components = 0;
            foreach (var node in source.Body.Concat(source.Blocks.SelectMany(block => block.Body)))
            {
                partials   += node.Kind == SavannaNodeKind.Include ? 1 : 0;
                components += node.Kind == SavannaNodeKind.Component ? 1 : 0;
            }

            var output = new StringBuilder();
            Start(output, index == 0 ? kind : "layout", source.Name, source.Name);
            output.Append("; renderer=compiled; minify=html; blocks=").Append(source.Blocks.Count)
                  .Append("; partials=").Append(partials).Append("; components=").Append(components);
            brief.Append(output).Append(" -->");
            detailed.Append(output).Append("; model=").Append(Escape(source.Model)).Append("; source=").Append(Escape(source.Path)).Append(" -->");
        }

        return (brief.ToString(), detailed.ToString());
    }

    public static (string Brief, string Detailed) Component(string type)
    {
        var syntax = SyntaxFactory.ParseTypeName(type);
        var name = syntax switch
        {
            QualifiedNameSyntax qualified  => qualified.Right.Identifier.ValueText,
            AliasQualifiedNameSyntax alias => alias.Name.Identifier.ValueText,
            SimpleNameSyntax simple        => simple.Identifier.ValueText,
            _                              => type,
        };
        var output = new StringBuilder();
        Start(output, "component", name, type);
        output.Append("; renderer=component");
        return (output + " -->", output + "; type=" + Escape(type) + " -->");
    }

    private static void Start(StringBuilder output, string kind, string name, string identity)
    {
        using var hash = SHA256.Create();
        var       id   = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(identity))).Replace("-", "").ToLowerInvariant().Substring(0, 12);
        output.Append("<!-- Savanna ").Append(kind).Append(": ").Append(Escape(name)).Append("; id=").Append(id).Append("; output=utf-8");
    }

    private static string Escape(string value) => value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
                                                       .Replace("-", "&#45;").Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
}
