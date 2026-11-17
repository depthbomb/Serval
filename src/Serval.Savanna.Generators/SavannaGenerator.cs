#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using System.Collections.Immutable;
using System.Text;
using Serval.Savanna.Compiler;
using Serval.Savanna.Compiler.CodeGeneration;
using Serval.Savanna.Compiler.Parsing;

namespace Serval.Savanna.Generators;

/// <summary>Generates typed views from Savanna additional files and project settings.</summary>
[Generator(LanguageNames.CSharp)]
public sealed class SavannaGenerator : IIncrementalGenerator
{
    private static readonly DiagnosticDescriptor InvalidTemplate      = new("SAVANNA001", "Invalid Savanna template", "{0}", "Savanna", DiagnosticSeverity.Error, true);
    private static readonly DiagnosticDescriptor InvalidReference     = new("SAVANNA002", "Invalid Savanna reference", "{0}", "Savanna", DiagnosticSeverity.Error, true);
    private static readonly DiagnosticDescriptor UnsafeContext        = new("SAVANNA003", "Unsafe Savanna HTML context", "{0}", "Savanna", DiagnosticSeverity.Error, true);
    private static readonly DiagnosticDescriptor InvalidConfiguration = new("SAVANNA004", "Invalid Savanna configuration", "{0}", "Savanna", DiagnosticSeverity.Error, true);

    /// <summary>Registers incremental template parsing, compilation, and diagnostic reporting.</summary>
    /// <param name="context">The Roslyn initialization context.</param>
    /// <remarks>
    ///     Reads SavannaRoot, SavannaNamespace, and MSBuildProjectDirectory from analyzer configuration. Runtime sources are
    ///     compared by generated text so unchanged views do not need to be re-emitted.
    /// </remarks>
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var settings = context.AnalyzerConfigOptionsProvider.Select(static (options, _) =>
        {
            options.GlobalOptions.TryGetValue("build_property.MSBuildProjectDirectory", out var directory);
            options.GlobalOptions.TryGetValue("build_property.SavannaRoot", out var root);
            options.GlobalOptions.TryGetValue("build_property.SavannaNamespace", out var generatedNamespace);
            return (Directory: directory ?? "", Root: root ?? "Views", Namespace: generatedNamespace ?? "Serval.Generated");
        });

        var documents = context.AdditionalTextsProvider.Where(static file => file.Path.EndsWith(".svn", StringComparison.OrdinalIgnoreCase))
                               .Combine(settings.Select(static (settings, _) => (settings.Directory, settings.Root)))
                               .Select(static (input, token) =>
                               {
                                   var file           = input.Left;
                                   var text           = file.GetText(token) ?? SourceText.From("");
                                   var directory      = input.Right.Directory.Replace('\\', '/');
                                   var configuredRoot = input.Right.Root.Replace('\\', '/');
                                   // Resolve both sides before containment checks; SavannaRoot may include .. or mixed separators.
                                   var root           = Path.GetFullPath(Path.Combine(directory, configuredRoot)).Replace('\\', '/').TrimEnd('/');
                                   var path           = Path.GetFullPath(file.Path.Replace('\\', '/')).Replace('\\', '/');
                                   var comparison     = Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                                   var name           = path.StartsWith(root + "/", comparison) ? path.Substring(root.Length + 1) : "../" + path;
                                   return SavannaParser.Parse(file.Path, name, text, token);
                               }).WithTrackingName("SavannaParse");

        var compilation = documents.Collect().Combine(settings.Select(static (settings, _) => settings.Namespace))
                                   .Select(static (input, token) => SavannaCompiler.Compile(input.Left, input.Right, token));
        var sources = compilation.SelectMany(static (result, _) => result.Sources.ToImmutableArray())
                                 .WithComparer(GeneratedSourceComparer.Instance).WithTrackingName("SavannaSource");

        context.RegisterSourceOutput(sources, static (output, source) => output.AddSource(source.HintName, SourceText.From(source.Text, Encoding.UTF8)));
        context.RegisterSourceOutput(compilation.Combine(documents.Collect()), static (output, input) =>
        {
            foreach (var diagnostic in input.Left.Diagnostics)
            {
                var document = input.Right.FirstOrDefault(document => document.Path == diagnostic.Path);
                var location = document is null ? Location.None : Location.Create(document.Path, diagnostic.Span, document.Text.Lines.GetLinePositionSpan(diagnostic.Span));
                var descriptor = diagnostic.Code switch
                {
                    "SAVANNA001" => InvalidTemplate,
                    "SAVANNA002" => InvalidReference,
                    "SAVANNA003" => UnsafeContext,
                    _            => InvalidConfiguration,
                };
                output.ReportDiagnostic(Diagnostic.Create(descriptor, location, diagnostic.Message));
            }
        });
    }

    private sealed class GeneratedSourceComparer : IEqualityComparer<SavannaGeneratedSource>
    {
        public static readonly GeneratedSourceComparer Instance = new();

        // Mappings can move while emitted C# stays identical. Only changes to compiler input need re-emission.
        public bool Equals(SavannaGeneratedSource? left, SavannaGeneratedSource? right)
            => ReferenceEquals(left, right) || left is not null && right is not null && left.HintName == right.HintName && left.Text == right.Text;

        public int GetHashCode(SavannaGeneratedSource value) => StringComparer.Ordinal.GetHashCode(value.Text);
    }
}
