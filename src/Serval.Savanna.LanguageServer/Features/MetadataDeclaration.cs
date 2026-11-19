#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using System.Security.Cryptography;
using System.Text;
using Serval.Savanna.LanguageServer.Rpc;

namespace Serval.Savanna.LanguageServer.Features;

internal static class MetadataDeclaration
{
    private static readonly SymbolDisplayFormat DeclarationFormat = new(
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters |
                         SymbolDisplayGenericsOptions.IncludeTypeConstraints,
        memberOptions: SymbolDisplayMemberOptions.IncludeAccessibility |
                       SymbolDisplayMemberOptions.IncludeModifiers     |
                       SymbolDisplayMemberOptions.IncludeType          |
                       SymbolDisplayMemberOptions.IncludeParameters,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType         |
                          SymbolDisplayParameterOptions.IncludeName         |
                          SymbolDisplayParameterOptions.IncludeParamsRefOut |
                          SymbolDisplayParameterOptions.IncludeDefaultValue,
        propertyStyle: SymbolDisplayPropertyStyle.ShowReadWriteDescriptor,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes          |
                              SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers |
                              SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    public static async Task<object?> CreateAsync(ISymbol symbol, CancellationToken cancellationToken)
    {
        if (!symbol.Locations.Any(location => location.IsInMetadata))
        {
            return null;
        }

        var type = symbol as INamedTypeSymbol ?? symbol.ContainingType;
        if (type is null)
        {
            return null;
        }

        type = type.OriginalDefinition;
        var selected = symbol.OriginalDefinition;
        var code     = new StringBuilder("// Metadata declarations. Method implementations are not included.\n// Assembly: ").AppendLine(type.ContainingAssembly.Identity.ToString());
        code.Append("// Type: ").AppendLine(type.ToDisplayString());
        code.AppendLine("#nullable enable");
        if (!type.ContainingNamespace.IsGlobalNamespace)
        {
            code.Append("namespace ").Append(type.ContainingNamespace.ToDisplayString()).AppendLine(";");
        }

        var kind = type.TypeKind switch { TypeKind.Interface => "interface", TypeKind.Struct => "struct", TypeKind.Enum => "enum", _ => "class" };
        code.Append("public ").Append(kind).Append(' ');
        var selection = new TextSpan(code.Length, type.Name.Length);
        code.Append(type.Name);
        if (type.TypeParameters.Length > 0)
        {
            code.Append('<').Append(string.Join(", ", type.TypeParameters.Select(parameter => parameter.Name))).Append('>');
        }

        code.AppendLine("\n{");
        foreach (var member in type.GetMembers().Where(member => member is { IsImplicitlyDeclared: false, DeclaredAccessibility: Accessibility.Public or Accessibility.Protected or Accessibility.ProtectedOrInternal }))
        {
            if (member is not (IMethodSymbol or IPropertySymbol or IFieldSymbol or IEventSymbol)
                          or IMethodSymbol { MethodKind: MethodKind.PropertyGet or MethodKind.PropertySet or MethodKind.EventAdd or MethodKind.EventRemove })
            {
                continue;
            }

            code.Append("    ");
            var start = code.Length;
            var parts = member.ToDisplayParts(DeclarationFormat);
            foreach (var part in parts)
            {
                if (SymbolEqualityComparer.Default.Equals(member, selected) && SymbolEqualityComparer.Default.Equals(part.Symbol?.OriginalDefinition, selected))
                {
                    selection = new TextSpan(code.Length, part.ToString().Length);
                }

                code.Append(part.ToString());
            }

            if (SymbolEqualityComparer.Default.Equals(member, selected) && selection.Start < start)
            {
                selection = new TextSpan(start, code.Length - start);
            }

            code.AppendLine(member is IPropertySymbol ? "" : ";");
        }

        code.AppendLine("}");
        var content   = code.ToString();
        var hash      = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
        var directory = Path.Combine(Path.GetTempPath(), "Serval.Savanna", "Metadata", hash);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, string.Concat(type.Name.Select(character => char.IsLetterOrDigit(character) ? character : '_')) + ".cs");
        if (!File.Exists(path) || !string.Equals(await File.ReadAllTextAsync(path, cancellationToken), content, StringComparison.Ordinal))
        {
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllTextAsync(temporary, content, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(temporary, path, true);
            }
            finally
            {
                File.Delete(temporary);
            }
        }

        return new
        {
            uri   = new Uri(path).AbsoluteUri,
            range = Position.Range(SourceText.From(content), selection),
        };
    }
}
