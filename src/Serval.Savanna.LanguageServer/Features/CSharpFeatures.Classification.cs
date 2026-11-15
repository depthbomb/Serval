#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.CodeAnalysis.Classification;
using Microsoft.CodeAnalysis.Text;

namespace Serval.Savanna.LanguageServer.Features;

public sealed partial class CSharpFeatures
{
    public async Task<IReadOnlyList<SemanticToken>> ClassifyAsync(CancellationToken cancellationToken)
    {
        var classified = await Classifier.GetClassifiedSpansAsync(_document, new TextSpan(0, _projection.Text.Length), cancellationToken);
        var tokens     = new List<SemanticToken>();
        foreach (var item in classified)
        {
            var span = MapSpan(item.TextSpan);
            var type = item.ClassificationType switch
            {
                ClassificationTypeNames.NamespaceName                                                                                                   => "namespace",
                ClassificationTypeNames.ClassName or ClassificationTypeNames.RecordClassName                                                            => "class",
                ClassificationTypeNames.StructName or ClassificationTypeNames.RecordStructName                                                          => "struct",
                ClassificationTypeNames.EnumName                                                                                                        => "enum",
                ClassificationTypeNames.InterfaceName                                                                                                   => "interface",
                ClassificationTypeNames.TypeParameterName                                                                                               => "typeParameter",
                ClassificationTypeNames.ParameterName                                                                                                   => "parameter",
                ClassificationTypeNames.LocalName or ClassificationTypeNames.FieldName or ClassificationTypeNames.Identifier                            => "variable",
                ClassificationTypeNames.PropertyName                                                                                                    => "property",
                ClassificationTypeNames.EnumMemberName                                                                                                  => "enumMember",
                ClassificationTypeNames.EventName                                                                                                       => "event",
                ClassificationTypeNames.MethodName or ClassificationTypeNames.ExtensionMethodName                                                       => "method",
                ClassificationTypeNames.Keyword or ClassificationTypeNames.ControlKeyword                                                               => "keyword",
                ClassificationTypeNames.Comment                                                                                                         => "comment",
                ClassificationTypeNames.StringLiteral or ClassificationTypeNames.VerbatimStringLiteral or ClassificationTypeNames.StringEscapeCharacter => "string",
                ClassificationTypeNames.NumericLiteral                                                                                                  => "number",
                ClassificationTypeNames.Operator or ClassificationTypeNames.OperatorOverloaded or ClassificationTypeNames.Punctuation                   => "operator",
                _                                                                                                                                       => null,
            };
            if (span is not null && type is not null)
            {
                tokens.Add(new SemanticToken(span.Value, type));
            }
        }

        return tokens;
    }
}
