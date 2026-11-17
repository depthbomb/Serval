#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System.Globalization;
using Serval.Routing;

namespace Serval.Generators;

internal sealed class BindingPlan
{
    public List<string> Statements { get; } = [];
    public List<string> Arguments  { get; } = [];
    public bool         ReadsBody  { get; set; }
    public bool         ReadsForm  { get; set; }
}

internal static class BindingEmitter
{
    private const string Runtime = "global::Serval.Binding.RequestBinding";

    public static bool IsBinding(AttributeData attribute)
        => attribute.AttributeClass?.ContainingNamespace.ToDisplayString() == "Serval.Binding"
           && attribute.AttributeClass.Name is "FromRouteAttribute" or "FromQueryAttribute" or "FromHeaderAttribute" or "FromBodyAttribute" or "FromFormAttribute" or "FromServicesAttribute";

    public static bool TryCreate(IMethodSymbol method, RoutePattern pattern, out BindingPlan plan, out string error)
    {
        plan  = new BindingPlan();
        error = "";
        foreach (var parameter in method.Parameters)
        {
            var attributes = parameter.GetAttributes().Where(IsBinding).ToArray();
            if (attributes.Length == 0)
            {
                plan.Arguments.Add(parameter.Type.ToDisplayString() == "Serval.RequestContext" ? "context" : "context.RequestAborted");
                continue;
            }

            if (attributes.Length != 1 || parameter.Type.ToDisplayString() is "Serval.RequestContext" or "System.Threading.CancellationToken")
            {
                error = $"Parameter '{parameter.Name}' must have one binding source; RequestContext and CancellationToken are supplied automatically.";
                return false;
            }

            var source   = attributes[0].AttributeClass!.Name.Substring(4).Replace("Attribute", "");
            var name     = attributes[0].ConstructorArguments.FirstOrDefault().Value as string ?? parameter.Name;
            var argument = "__argument" + parameter.Ordinal;
            var type     = parameter.Type;
            var typeName = TypeName(type);
            var nullable = IsNullable(type);
            var optional = nullable || parameter.HasExplicitDefaultValue;
            var fallback = parameter.HasExplicitDefaultValue ? DefaultValue(parameter) : "default(" + typeName + ")";

            plan.Arguments.Add(argument);

            if (source == "Body")
            {
                if (plan.ReadsBody || parameter.HasExplicitDefaultValue)
                {
                    error = "An action can have only one body parameter, and the body is required rather than defaulted.";
                    return false;
                }

                plan.ReadsBody = true;
                plan.Statements.Add($"var {argument} = await context.Request.ReadJsonAsync<{typeName}>(context.RequestAborted);");
                if (attributes[0].NamedArguments.Any(pair => pair.Key == "Validate" && pair.Value.Value is true))
                {
                    plan.Statements.Add($"await global::Serval.Validation.InputValidation.ValidateAsync(context, {argument});");
                }
                continue;
            }

            if (source == "Services")
            {
                var serviceType = type.TypeKind == TypeKind.Dynamic ? "global::System.Object" : TypeName(type.WithNullableAnnotation(NullableAnnotation.NotAnnotated));
                var service     = $"context.Services.GetService(typeof({serviceType}))";
                plan.Statements.Add(optional
                    ? $"{typeName} {argument} = {service} is {{ }} __service{parameter.Ordinal} ? ({typeName})__service{parameter.Ordinal} : {fallback};"
                    : $"var {argument} = global::Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<{typeName}>(context.Services);");
                continue;
            }

            if (source == "Form")
            {
                if (string.IsNullOrWhiteSpace(name))
                {
                    error = $"Parameter '{parameter.Name}' has an empty binding name.";
                    return false;
                }

                if (plan is { ReadsBody: true, ReadsForm: false })
                {
                    error = "FromForm and FromBody cannot be combined on the same action.";
                    return false;
                }

                if (!plan.ReadsForm)
                {
                    plan.Statements.Add("await context.Request.ReadFormAsync(context.RequestAborted);");
                    plan.ReadsForm = true;
                    plan.ReadsBody = true;
                }

                var formName = ControllerGenerator.Literal(name);
                if (type.WithNullableAnnotation(NullableAnnotation.NotAnnotated).ToDisplayString() == "Serval.Http.UploadedFile")
                {
                    var file = $"(await context.Request.ReadFormAsync(context.RequestAborted)).GetFile({formName})";
                    plan.Statements.Add($"{typeName} {argument} = {file}" + (optional ? $" ?? {fallback};" : " ?? throw new global::Serval.Http.HttpErrorException(global::Serval.Http.Constants.StatusCodes.BadRequest, \"An uploaded file is required.\");"));
                    continue;
                }

                if (type is IArrayTypeSymbol { Rank: 1 } files && files.ElementType.ToDisplayString() == "Serval.Http.UploadedFile")
                {
                    var uploaded = "__files" + parameter.Ordinal;
                    plan.Statements.Add($"var {uploaded} = (await context.Request.ReadFormAsync(context.RequestAborted)).GetFiles({formName});");
                    var fileArray = $"global::System.Linq.Enumerable.ToArray({uploaded})";
                    plan.Statements.Add($"{typeName} {argument} = " + (optional ? $"{uploaded}.Count == 0 ? {fallback} : {fileArray};" : fileArray + ";"));
                    continue;
                }
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                error = $"Parameter '{parameter.Name}' has an empty binding name.";
                return false;
            }

            if (source == "Route")
            {
                var segment = pattern.Segments.FirstOrDefault(segment => string.Equals(segment.Name, name, StringComparison.OrdinalIgnoreCase));
                if (segment is null || segment is { Optional: true, Default: null, CatchAll: 0 } && !optional)
                {
                    error = segment is null
                        ? $"Route '{pattern.Text}' has no parameter named '{name}'."
                        : $"Optional route parameter '{name}' needs a nullable action parameter or a default value.";
                    return false;
                }
            }

            var sourceLiteral = ControllerGenerator.Literal(source);
            var nameLiteral   = ControllerGenerator.Literal(name);
            if (type is IArrayTypeSymbol array)
            {
                if (source == "Route" || array.Rank != 1 || IsNullable(array.ElementType) || !CanParse(array.ElementType))
                {
                    error = $"Parameter '{parameter.Name}' must be a scalar, or a one-dimensional query/header array of supported scalar values.";
                    return false;
                }

                var values  = "__values" + parameter.Ordinal;
                var element = TypeName(array.ElementType);
                plan.Statements.Add($"var {values} = {Runtime}.ReadMany(context, {sourceLiteral}, {nameLiteral});");
                var allocation = $"new {element}[{values}.Count]";
                plan.Statements.Add($"{typeName} {argument} = " + (optional ? $"{values}.Count == 0 ? {fallback} : {allocation};" : allocation + ";"));
                plan.Statements.Add($"for (var __index{parameter.Ordinal} = 0; __index{parameter.Ordinal} < {values}.Count; __index{parameter.Ordinal}++)\n            {{\n                {argument}![__index{parameter.Ordinal}] = {Parse(array.ElementType, $"{values}[__index{parameter.Ordinal}]", sourceLiteral, nameLiteral)};\n            }}");
                continue;
            }

            if (!CanParse(Unwrap(type)))
            {
                error = $"Parameter '{parameter.Name}' must be a string, an enum, or implement IParsable<T>. Use FromBody for JSON objects.";
                return false;
            }

            var value = "__value" + parameter.Ordinal;
            plan.Statements.Add($"var {value} = {Runtime}.Read(context, {sourceLiteral}, {nameLiteral});");
            var parsed = Parse(Unwrap(type), value, sourceLiteral, nameLiteral);
            plan.Statements.Add($"{typeName} {argument} = " + (optional ? $"{value} is null ? {fallback} : {parsed};" : parsed + ";"));
        }

        return true;
    }

    public static string TypeName(ITypeSymbol type)
        => type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat.WithMiscellaneousOptions(
            SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier));

    private static bool IsNullable(ITypeSymbol type) => type.NullableAnnotation == NullableAnnotation.Annotated || IsNullableValue(type);

    private static bool IsNullableValue(ITypeSymbol type) => type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T };

    private static ITypeSymbol Unwrap(ITypeSymbol type) => IsNullableValue(type) ? ((INamedTypeSymbol)type).TypeArguments[0] : type;

    private static bool CanParse(ITypeSymbol type)
        => type.SpecialType == SpecialType.System_String || type.TypeKind == TypeKind.Enum || type.AllInterfaces.Any(contract => contract.OriginalDefinition.ToDisplayString() == "System.IParsable<TSelf>" && SymbolEqualityComparer.Default.Equals(contract.TypeArguments[0], type));

    private static string Parse(ITypeSymbol type, string value, string source, string name)
        => type.SpecialType == SpecialType.System_String
            ? $"{Runtime}.Require({value}, {source}, {name})"
            : $"{Runtime}.{(type.TypeKind == TypeKind.Enum ? "ParseEnum" : "Parse")}<{TypeName(type.WithNullableAnnotation(NullableAnnotation.NotAnnotated))}>({value}, {source}, {name})";

    private static string DefaultValue(IParameterSymbol parameter)
    {
        var value = parameter.ExplicitDefaultValue;
        if (value is null)
        {
            return "default(" + TypeName(parameter.Type) + ")" + (parameter.Type.IsReferenceType && !IsNullable(parameter.Type) ? "!" : "");
        }

        var literal = value switch
        {
            string text                                          => ControllerGenerator.Literal(text),
            char character                                       => SymbolDisplay.FormatLiteral(character, true),
            bool boolean                                         => boolean ? "true" : "false",
            float.NaN                                            => "global::System.Single.NaN",
            float number when float.IsPositiveInfinity(number)   => "global::System.Single.PositiveInfinity",
            float number when float.IsNegativeInfinity(number)   => "global::System.Single.NegativeInfinity",
            double.NaN                                           => "global::System.Double.NaN",
            double number when double.IsPositiveInfinity(number) => "global::System.Double.PositiveInfinity",
            double number when double.IsNegativeInfinity(number) => "global::System.Double.NegativeInfinity",
            float number                                         => number.ToString("R", CultureInfo.InvariantCulture) + "F",
            double number                                        => number.ToString("R", CultureInfo.InvariantCulture) + "D",
            decimal number                                       => number.ToString(CultureInfo.InvariantCulture)      + "M",
            ulong number                                         => number.ToString(CultureInfo.InvariantCulture)      + "UL",
            long number                                          => number.ToString(CultureInfo.InvariantCulture)      + "L",
            uint number                                          => number.ToString(CultureInfo.InvariantCulture)      + "U",
            _                                                    => Convert.ToString(value, CultureInfo.InvariantCulture)!,
        };
        return Unwrap(parameter.Type).TypeKind == TypeKind.Enum ? $"({TypeName(parameter.Type)})({literal})" : literal;
    }
}
