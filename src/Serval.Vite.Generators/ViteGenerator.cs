#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;

namespace Serval.Vite.Generators;

/// <summary>Generates C# asset lookups from a Vite build manifest or development source paths.</summary>
[Generator(LanguageNames.CSharp)]
public sealed class ViteGenerator : IIncrementalGenerator
{
    private static readonly DiagnosticDescriptor MissingManifest      = new("VITE001", "Vite manifest is unavailable", "{0}", "Vite", DiagnosticSeverity.Error, true);
    private static readonly DiagnosticDescriptor InvalidManifest      = new("VITE002", "Invalid Vite manifest", "{0}", "Vite", DiagnosticSeverity.Error, true);
    private static readonly DiagnosticDescriptor InvalidConfiguration = new("VITE003", "Invalid Vite configuration", "{0}", "Vite", DiagnosticSeverity.Error, true);

    /// <summary>Registers manifest reading, dependency resolution, and deterministic source generation.</summary>
    /// <param name="context">The compiler's incremental generator registration context.</param>
    /// <remarks>
    ///     Reads the ViteManifest additional file and ViteNamespace, ViteClassName, and ViteBaseUrl properties.
    ///     With ViteDevelopment enabled, uses ViteDevEntries, ViteDevAssets, and ViteDevServerUrl instead of a manifest.
    ///     The package's MSBuild targets supply these values. No frontend processes run in the generator.
    /// </remarks>
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var settings = context.AnalyzerConfigOptionsProvider.Select(static (provider, _) =>
        {
            var options = provider.GlobalOptions;
            if (!options.TryGetValue("build_property.ViteManifestFullPath", out var manifest))
            {
                options.TryGetValue("build_property.ViteManifest", out manifest);
            }

            options.TryGetValue("build_property.ViteNamespace", out var generatedNamespace);
            options.TryGetValue("build_property.ViteClassName", out var className);
            options.TryGetValue("build_property.ViteBaseUrl", out var baseUrl);
            options.TryGetValue("build_property.ViteDevelopment", out var development);
            options.TryGetValue("build_property.ViteDevServerUrl", out var devServerUrl);
            options.TryGetValue("build_property.ViteDevEntries", out var devEntries);
            options.TryGetValue("build_property.ViteDevAssets", out var devAssets);
            return (Manifest: manifest ?? "", Namespace: generatedNamespace ?? "Serval.Generated", ClassName: className ?? "Assets", BaseUrl: baseUrl ?? "/",
                    Development: development is null or "" ? "false" : development,
                    DevServerUrl: devServerUrl is null or "" ? "/" : devServerUrl, DevEntries: devEntries ?? "", DevAssets: devAssets ?? "");
        });

        // Development reads source paths from settings. A stale or absent build manifest must not get involved.
        var documents = context.AdditionalTextsProvider.Combine(settings.Select(static (value, _) =>
                                   string.Equals(value.Development, "true", StringComparison.OrdinalIgnoreCase) ? "" : value.Manifest))
                               .Where(static input => SamePath(input.Left.Path, input.Right))
                               .Select(static (input, token) => (input.Left.Path, Text: input.Left.GetText(token)?.ToString()));
        // Strings and tuples give the pipeline value equality, so ordinary C# edits do not redo manifest work.
        var result = documents.Collect().Combine(settings).Select(static (input, token) =>
        {
            var configuration = input.Right;
            return Generate(input.Left, configuration.Manifest, configuration.Namespace, configuration.ClassName, configuration.BaseUrl,
                configuration.Development, configuration.DevServerUrl, configuration.DevEntries, configuration.DevAssets, token);
        }).WithTrackingName("ViteSource");
        context.RegisterSourceOutput(result, static (output, value) =>
        {
            if (value.Error.Length != 0)
            {
                var descriptor = value.Code switch
                {
                    "VITE001" => MissingManifest,
                    "VITE002" => InvalidManifest,
                    _         => InvalidConfiguration,
                };
                var location = value.Path.Length == 0 ? Location.None : Location.Create(value.Path, default, default);
                output.ReportDiagnostic(Diagnostic.Create(descriptor, location, value.Error));
            }

            if (value.Source.Length != 0)
            {
                output.AddSource(value.Hint, SourceText.From(value.Source, Encoding.UTF8));
            }
        });
    }

    private static (string Source, string Hint, string Code, string Error, string Path) Generate(
        ImmutableArray<(string Path, string? Text)> documents,
        string                                      manifest,
        string                                      generatedNamespace,
        string                                      className,
        string                                      baseUrl,
        string                                      development,
        string                                      devServerUrl,
        string                                      devEntries,
        string                                      devAssets,
        CancellationToken                           cancellationToken)
    {
        // An analyzer can flow through transitive package references without its build targets.
        if (manifest.Length == 0 && string.Equals(development, "false", StringComparison.OrdinalIgnoreCase))
        {
            return ("", "", "", "", "");
        }

        if (!IsIdentifier(className) || generatedNamespace.Length != 0 && !generatedNamespace.Split('.').All(IsIdentifier))
        {
            return ("", "", "VITE003", "ViteClassName must be a C# identifier and ViteNamespace must be a dotted C# namespace.", "");
        }

        // The C# lexer ignores formatting characters when comparing identifiers.
        var identifier = SyntaxFactory.ParseToken(className).ValueText;
        if (identifier is "Urls" or "Entries" or "GetUrl" or "TryGetUrl" or "GetEntry" or "TryGetEntry" or "IsDevelopment" or "_urls" or "_entries")
        {
            return ("", "", "VITE003", "ViteClassName cannot have the same name as a generated lookup member.", "");
        }

        if (!bool.TryParse(development, out var isDevelopment))
        {
            return ("", "", "VITE003", "ViteDevelopment must be true or false.", "");
        }

        if (isDevelopment)
        {
            try
            {
                var chunks = ViteDevelopment.Create(devServerUrl, devEntries, devAssets, cancellationToken);
                var source = ViteEmitter.Emit(chunks, generatedNamespace, className, devServerUrl, cancellationToken, true);
                return (source, identifier + ".g.cs", "", "", "");
            }
            catch (ArgumentException exception)
            {
                return ("", "", "VITE003", exception.Message, "");
            }
        }

        if (documents.Length != 1 || documents[0].Text is null)
        {
            return ("", "", "VITE001", $"Expected one readable ViteManifest additional file at '{manifest}'. Build the frontend before compiling the backend.", manifest);
        }

        try
        {
            var chunks = ViteManifest.Parse(documents[0].Text!, cancellationToken);
            var source = ViteEmitter.Emit(chunks, generatedNamespace, className, baseUrl, cancellationToken);
            return (source, identifier + ".g.cs", "", "", "");
        }
        catch (JsonException exception)
        {
            return ("", "", "VITE002", exception.Message, manifest);
        }
        catch (ManifestException exception)
        {
            return ("", "", "VITE002", exception.Message, manifest);
        }
    }

    private static bool SamePath(string left, string right)
        => string.Equals(left.Replace('\\', '/'), right.Replace('\\', '/'), Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static bool IsIdentifier(string value)
        => SyntaxFacts.IsValidIdentifier(value) && SyntaxFacts.GetKeywordKind(value) == SyntaxKind.None;
}
