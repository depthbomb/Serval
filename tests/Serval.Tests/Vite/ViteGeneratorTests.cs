#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using System.Reflection;
using System.Text.Json;
using Serval.Tests.Support;
using Serval.Vite;
using Serval.Vite.Generators;

namespace Serval.Tests.Vite;

public sealed class ViteGeneratorTests
{
    private const           string             ManifestPath   = "/project/dist/.vite/manifest.json";
    private const           string             SimpleManifest = """{"src/main.ts":{"file":"assets/main-123.js","isEntry":true}}""";
    private static readonly CSharpParseOptions ParseOptions   = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview);
    private static readonly MetadataReference[] References = [.. ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
                                                                                                                         .Append(typeof(ViteEntry).Assembly.Location).Distinct(StringComparer.OrdinalIgnoreCase).Select<string, MetadataReference>(path => MetadataReference.CreateFromFile(path))];

    [Fact]
    public void EntryGraphPreservesCssOrderAndKeepsDynamicImportsLazy()
    {
        const string manifest = """
                                {
                                  "src/main.ts": {"file":"///assets/main.js","isEntry":true,"css":["assets/main.css"],"imports":["_a.js","_b.js"],"dynamicImports":["src/lazy.ts"]},
                                  "_a.js": {"file":"assets/a.js","imports":["_shared.js"],"css":["assets/a.css","assets/shared.css"]},
                                  "_b.js": {"file":"assets/b.js","imports":["_shared.js","_shared-alias.js"],"css":["assets/b.css"]},
                                  "_shared-alias.js": {"file":"///assets/shared.js","css":["/assets/shared.css"]},
                                  "_shared.js": {"file":"assets/shared.js","imports":["src/main.ts"],"css":["assets/shared.css"]},
                                  "src/lazy.ts": {"file":"assets/lazy.js","isDynamicEntry":true,"css":["assets/lazy.css"]},
                                  "src/other.ts": {"file":"assets/other.js","isEntry":true,"imports":["src/main.ts"]},
                                  "src/logo.svg": {"file":"assets/logo.svg","assets":["assets/unlisted.png"],"names":["logo"],"future":{"anything":true}},
                                  "style.css": {"file":"assets/style.css","isEntry":true},
                                  "src/unstyled.ts": {"file":"assets/unstyled.js","isEntry":true}
                                }
                                """;
        var entries = Run<IReadOnlyDictionary<string, ViteEntry>>(manifest, "Assets.Entries");
        Assert.Equal(4, entries.Count);
        var main = entries["src/main.ts"];
        Assert.Equal(["/assets/main.js"], main.Scripts);
        Assert.Equal(["/assets/main.css", "/assets/shared.css", "/assets/a.css", "/assets/b.css"], main.Stylesheets);
        Assert.Equal(["/assets/shared.js", "/assets/a.js", "/assets/b.js"], main.ModulePreloads);
        Assert.Equal(["/assets/shared.js", "/assets/a.js", "/assets/b.js", "/assets/main.js"], entries["src/other.ts"].ModulePreloads);
        Assert.Equal(["/assets/style.css"], entries["style.css"].Stylesheets);
        Assert.Empty(entries["src/unstyled.ts"].Stylesheets);
    }

    [Fact]
    public void CssEntryAndCombinedStylesheetAreSupported()
    {
        const string manifest = """
                                {
                                  "src/site.css":{"file":"assets/site.css","isEntry":true,"css":["assets/site.css"],"imports":["_shared.css"]},
                                  "_shared.css":{"file":"assets/shared.css"},
                                  "src/main.ts":{"file":"assets/main.js","isEntry":true},
                                  "style.css":{"file":"assets/all.css"}
                                }
                                """;
        var entries = Run<IReadOnlyDictionary<string, ViteEntry>>(manifest, "Assets.Entries");
        Assert.Empty(entries["src/site.css"].Scripts);
        Assert.Empty(entries["src/site.css"].ModulePreloads);
        Assert.Equal(["/assets/site.css", "/assets/shared.css", "/assets/all.css"], entries["src/site.css"].Stylesheets);
        Assert.Equal(["/assets/all.css"], entries["src/main.ts"].Stylesheets);
    }

    [Fact]
    public void ValidUnicodeNamesStillGenerateCompilableSources()
    {
        Assert.Equal("/assets/main-123.js", Run<string>(SimpleManifest, "Assets.GetUrl(\"src/main.ts\")",
            new Dictionary<string, string> { ["ViteClassName"] = "A\u200cssets" }));
        var urls = Run<IReadOnlyDictionary<string, string>>("{\"src/\\uD83C\\uDF3F.svg\":{\"file\":\"assets/\\uD83C\\uDF3F.svg\"}}", "Assets.Urls");
        Assert.Equal("/assets/🌿.svg", urls["src/🌿.svg"]);
    }

    [Fact]
    public void ExactLookupsHavePredictableMissingKeyAndNullBehavior()
    {
        const string manifest = """
                                {"src/main.ts":{"file":"assets/main.js","isEntry":true},"src/logo.svg":{"file":"assets/logo.svg"}}
                                """;
        var results = Run<bool[]>(manifest, """
                                            new bool[]
                                            {
                                                Assets.GetUrl("src/logo.svg") == "/assets/logo.svg",
                                                !Assets.TryGetUrl("logo.svg", out var missing) && missing is null,
                                                !Assets.TryGetUrl("SRC/logo.svg", out _),
                                                !Assets.TryGetEntry("src/logo.svg", out var asset) && asset is null,
                                                Assets.TryGetEntry("src/main.ts", out var main) && object.ReferenceEquals(main, Assets.GetEntry("src/main.ts")),
                                                Throws<System.Collections.Generic.KeyNotFoundException>(() => Assets.GetUrl("missing")),
                                                Throws<System.Collections.Generic.KeyNotFoundException>(() => Assets.GetEntry("src/logo.svg")),
                                                Throws<System.ArgumentNullException>(() => Assets.GetUrl(null!))
                                            }
                                            """);
        Assert.All(results, Assert.True);
    }

    [Theory]
    [InlineData("/", "/assets/main-123.js")]
    [InlineData("/build/", "/build/assets/main-123.js")]
    [InlineData("https://cdn.example/build", "https://cdn.example/build/assets/main-123.js")]
    [InlineData("https://cdn.example/build///", "https://cdn.example/build/assets/main-123.js")]
    [InlineData("./", "./assets/main-123.js")]
    [InlineData("", "assets/main-123.js")]
    public void BaseUrlIsJoinedOnce(string baseUrl, string expected)
        => Assert.Equal(expected, Run<string>(SimpleManifest, "Assets.GetUrl(\"src/main.ts\")", new Dictionary<string, string> { ["ViteBaseUrl"] = baseUrl }));

    [Fact]
    public void ManifestStringsAreEscapedAsCSharpLiteralsWithoutChangingTheirValue()
    {
        const string key      = "src/a\"\\\n雪.svg";
        const string file     = "assets/a\"\\\n雪<&>.svg";
        var          manifest = JsonSerializer.Serialize(new Dictionary<string, object> { [key] = new { file } });
        var          urls     = Run<IReadOnlyDictionary<string, string>>(manifest, "Assets.Urls");
        Assert.Equal("/" + file, urls[key]);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{")]
    [InlineData("{\"src/main.ts\":[\"assets/main.js\"]}")]
    [InlineData("{\"a\":{}}")]
    [InlineData("{\"a\":{\"file\":\"\"}}")]
    [InlineData("{\"a\":{\"file\":3}}")]
    [InlineData("{\"a\":{\"file\":\"a.js\",\"isEntry\":\"true\"}}")]
    [InlineData("{\"a\":{\"file\":\"a.js\",\"isDynamicEntry\":null}}")]
    [InlineData("{\"a\":{\"file\":\"a.js\",\"src\":false}}")]
    [InlineData("{\"a\":{\"file\":\"a.js\",\"css\":{}}}")]
    [InlineData("{\"a\":{\"file\":\"a.js\",\"assets\":[null]}}")]
    [InlineData("{\"a\":{\"file\":\"a.js\",\"imports\":[\"\"]}}")]
    [InlineData("{\"a\":{\"file\":\"a.js\",\"imports\":[\"missing\"]}}")]
    [InlineData("{\"a\":{\"file\":\"a.js\",\"dynamicImports\":[\"missing\"]}}")]
    [InlineData("{\"a\":{\"file\":\"a.js\"},\"a\":{\"file\":\"b.js\"}}")]
    [InlineData("{\"a\":{\"file\":\"a.js\",\"file\":\"b.js\"}}")]
    [InlineData("{\"a\":{\"file\":\"\\uD800\"}}")]
    [InlineData("{\"\\uDFFF\":{\"file\":\"a.js\"}}")]
    [InlineData("{\"a\":{\"file\":\"a.js\",\"css\":[\"\\uD800\"]}}")]
    public void InvalidInputProducesManifestDiagnosticsInsteadOfBrokenCode(string manifest)
    {
        var result = Driver(manifest).RunGenerators(Compilation()).GetRunResult();
        Assert.Equal("VITE002", Assert.Single(result.Diagnostics).Id);
        Assert.Null(Assert.Single(result.Results).Exception);
        Assert.Equal(ManifestPath, result.Diagnostics[0].Location.GetLineSpan().Path);
        Assert.Empty(result.GeneratedTrees);
    }

    [Theory]
    [InlineData("ViteClassName", "Bad-Name")]
    [InlineData("ViteClassName", "class")]
    [InlineData("ViteClassName", "Urls")]
    [InlineData("ViteClassName", "GetEntry")]
    [InlineData("ViteClassName", "IsDevelopment")]
    [InlineData("ViteClassName", "Url\u200cs")]
    [InlineData("ViteClassName", "Get\u200cEntry")]
    [InlineData("ViteClassName", "")]
    [InlineData("ViteNamespace", "Bad..Name")]
    [InlineData("ViteNamespace", "class.Invalid")]
    [InlineData("ViteNamespace", "Valid; class Injected {}")]
    [InlineData("ViteDevelopment", "sometimes")]
    public void InvalidConfigurationProducesDiagnostics(string key, string value)
    {
        var result = Driver(SimpleManifest, new Dictionary<string, string> { [key] = value }).RunGenerators(Compilation()).GetRunResult();
        Assert.Equal("VITE003", Assert.Single(result.Diagnostics).Id);
        Assert.Empty(result.GeneratedTrees);
    }

    [Theory]
    [InlineData("/", "/", null)]
    [InlineData("/_vite///", "/_vite/", "{broken manifest")]
    [InlineData("http://localhost:5173/frontend/", "http://localhost:5173/frontend/", "{}")]
    public void DevelopmentLoadsSourceModulesAndHmrWithoutAManifest(string serverUrl, string prefix, string? manifest)
    {
        var values = Run<object[]>(manifest, "new object[] { Assets.IsDevelopment, Assets.Entries, Assets.Urls }", new Dictionary<string, string>
        {
            ["ViteDevelopment"]  = "true",
            ["ViteDevServerUrl"] = serverUrl,
            ["ViteDevEntries"]   = "src/main.js;src/theme.css;src/main.js",
            ["ViteDevAssets"]    = "src/logo #1%.svg;src/main.js",
        });
        Assert.True((bool)values[0]);
        var entries = (IReadOnlyDictionary<string, ViteEntry>)values[1];
        var urls    = (IReadOnlyDictionary<string, string>)values[2];
        Assert.Equal(2, entries.Count);
        Assert.Equal(3, urls.Count);
        foreach (var key in new[] { "src/main.js", "src/theme.css" })
        {
            Assert.Equal([prefix + "@vite/client", prefix + key + "?import"], entries[key].Scripts);
            Assert.Empty(entries[key].Stylesheets);
            Assert.Empty(entries[key].ModulePreloads);
        }

        Assert.Equal(prefix + "src/logo%20%231%25.svg", urls["src/logo #1%.svg"]);
    }

    [Theory]
    [InlineData("ViteDevEntries", "")]
    [InlineData("ViteDevEntries", "../main.js")]
    [InlineData("ViteDevEntries", "/src/main.js")]
    [InlineData("ViteDevEntries", "src\\main.js")]
    [InlineData("ViteDevEntries", "src/\\uD800.js")]
    [InlineData("ViteDevAssets", "src/../secret")]
    [InlineData("ViteDevServerUrl", "//other.example")]
    [InlineData("ViteDevServerUrl", "javascript:alert(1)")]
    [InlineData("ViteDevServerUrl", "http://user:password@localhost:5173")]
    [InlineData("ViteDevServerUrl", "/frontend?query")]
    [InlineData("ViteDevServerUrl", "/frontend#fragment")]
    public void InvalidDevelopmentConfigurationProducesDiagnostics(string key, string value)
    {
        var options = new Dictionary<string, string> { ["ViteDevelopment"] = "true", ["ViteDevEntries"] = "src/main.js" };
        // Attribute strings use UTF-8 metadata, so construct the invalid UTF-16 case at runtime.
        options[key] = value.Replace("\\uD800", "\ud800");
        var result = Driver("{broken manifest", options).RunGenerators(Compilation()).GetRunResult();
        Assert.Equal("VITE003", Assert.Single(result.Diagnostics).Id);
        Assert.Null(Assert.Single(result.Results).Exception);
        Assert.Empty(result.GeneratedTrees);
    }

    [Fact]
    public void CustomClassAndNamespaceCompile()
        => Assert.Equal("/assets/main-123.js", Run<string>(SimpleManifest, "Other.Generated.Frontend.GetUrl(\"src/main.ts\")",
            new Dictionary<string, string> { ["ViteNamespace"] = "Other.Generated", ["ViteClassName"] = "Frontend" }, false));

    [Fact]
    public void ResolvedPathTakesPrecedenceOverRelativeCommandLineProperty()
        => Assert.Equal("/assets/main-123.js", Run<string>(SimpleManifest, "Assets.GetUrl(\"src/main.ts\")",
            new Dictionary<string, string> { ["ViteManifest"] = "dist/.vite/manifest.json", ["ViteManifestFullPath"] = ManifestPath }));

    [Fact]
    public void EmptyManifestCompilesWithoutSpecialCases()
        => Assert.Equal(0, Run<int>("{}", "Assets.Urls.Count + Assets.Entries.Count"));

    [Fact]
    public void GenerationIsDeterministicRegardlessOfManifestKeyOrder()
    {
        var first  = Driver("""{"z":{"file":"z.js"},"a":{"file":"a.js","isEntry":true,"imports":["z"]}}""").RunGenerators(Compilation()).GetRunResult();
        var second = Driver("""{"a":{"imports":["z"],"isEntry":true,"file":"a.js"},"z":{"file":"z.js"}}""").RunGenerators(Compilation()).GetRunResult();
        Assert.Equal(first.GeneratedTrees.Single().ToString(), second.GeneratedTrees.Single().ToString());
    }

    [Fact]
    public void ChangesRegenerateAndRemovalDoesNotLeaveStaleSource()
    {
        const string manifest    = """{"src/main.js":{"file":"assets/main-123.js","isEntry":true,"imports":["shared"]},"shared":{"file":"shared.js"}}""";
        var          file        = new ManifestFile(manifest);
        var          driver      = Driver(file, new Dictionary<string, string> { ["ViteBaseUrl"] = "/before" });
        var          compilation = Compilation();
        driver = driver.RunGenerators(compilation);
        Assert.Contains("main-123.js", driver.GetRunResult().GeneratedTrees.Single().ToString());
        driver = driver.WithUpdatedAnalyzerConfigOptions(new ViteOptions(new Dictionary<string, string> { ["ViteBaseUrl"] = "/after" })).RunGenerators(compilation);
        var source = Assert.Single(driver.GetRunResult().GeneratedTrees).ToString();
        Assert.Contains("\"/after/shared.js\"", source);
        Assert.DoesNotContain("/before/", source);
        var replacement = new ManifestFile(manifest.Replace("main-123.js", "main-456.js"));
        driver = driver.ReplaceAdditionalText(file, replacement).RunGenerators(compilation);
        Assert.Contains("main-456.js", driver.GetRunResult().GeneratedTrees.Single().ToString());
        Assert.DoesNotContain("main-123.js", driver.GetRunResult().GeneratedTrees.Single().ToString());
        driver = driver.RemoveAdditionalTexts([replacement]).RunGenerators(compilation);
        Assert.Equal("VITE001", Assert.Single(driver.GetRunResult().Diagnostics).Id);
        Assert.Empty(driver.GetRunResult().GeneratedTrees);

        var development = new Dictionary<string, string> { ["ViteDevelopment"] = "true", ["ViteDevEntries"] = "src/main.js" };
        driver = driver.WithUpdatedAnalyzerConfigOptions(new ViteOptions(development)).RunGenerators(compilation);
        Assert.Empty(driver.GetRunResult().Diagnostics);
        Assert.Contains("/@vite/client", Assert.Single(driver.GetRunResult().GeneratedTrees).ToString());
        development["ViteDevEntries"]   = "src/other.js";
        development["ViteDevServerUrl"] = "/frontend/";
        driver                          = driver.WithUpdatedAnalyzerConfigOptions(new ViteOptions(development)).RunGenerators(compilation);
        source                          = Assert.Single(driver.GetRunResult().GeneratedTrees).ToString();
        Assert.Contains("/frontend/src/other.js?import", source);
        Assert.DoesNotContain("src/main.js", source);
        driver = driver.WithUpdatedAnalyzerConfigOptions(new ViteOptions(null)).RunGenerators(compilation);
        Assert.Equal("VITE001", Assert.Single(driver.GetRunResult().Diagnostics).Id);
        Assert.Empty(driver.GetRunResult().GeneratedTrees);
    }

    [Fact]
    public void UnrelatedCSharpEditsReuseTheGeneratedSource()
    {
        var driver = Driver(SimpleManifest).RunGenerators(Compilation());
        driver = driver.RunGenerators(Compilation("public class Unrelated {}"));
        var output = Assert.Single(driver.GetRunResult().Results).TrackedSteps["ViteSource"].SelectMany(step => step.Outputs);
        Assert.All(output, item => Assert.Equal(IncrementalStepRunReason.Cached, item.Reason));
    }

    [Fact]
    public void MissingAndUnreadableManifestHaveActionableDiagnostics()
    {
        var result = Driver(new ManifestFile(null)).RunGenerators(Compilation()).GetRunResult();
        Assert.Equal("VITE001", Assert.Single(result.Diagnostics).Id);
        Assert.Contains("Build the frontend", result.Diagnostics[0].GetMessage());
    }

    [Fact]
    public void TransitiveAnalyzerWithoutConfigurationDoesNothing()
    {
        var result = Driver(SimpleManifest, new Dictionary<string, string> { ["ViteManifest"] = "" }).RunGenerators(Compilation()).GetRunResult();
        Assert.Empty(result.Diagnostics);
        Assert.Empty(result.GeneratedTrees);
    }

    [Fact]
    public void DeepCyclicGraphDoesNotUseRecursiveTraversal()
    {
        var chunks = Enumerable.Range(0, 3000).ToDictionary(index => $"chunk{index}", index => new
        {
            file    = $"assets/{index}.js",
            isEntry = index == 0,
            imports = new[] { $"chunk{(index + 1) % 3000}" },
        });
        var result = Driver(JsonSerializer.Serialize(chunks)).RunGenerators(Compilation()).GetRunResult();
        Assert.Empty(result.Diagnostics);
        var source = Assert.Single(result.GeneratedTrees).ToString();
        Assert.Contains("/assets/2999.js", source);
        Assert.Equal(2, source.Split("\"/assets/2999.js\"").Length - 1);
    }

    [Fact]
    public void EntrySnapshotsCannotBeMutatedByCallers()
    {
        var scripts = new[] { "/main.js" };
        var entry   = new ViteEntry(scripts, [], []);
        scripts[0] = "changed";
        Assert.Equal("/main.js", entry.Scripts[0]);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)entry.Scripts)[0] = "changed");
        Assert.Throws<ArgumentNullException>(() => new ViteEntry(null!, [], []));
    }

    private static T Run<T>(string? manifest, string expression, Dictionary<string, string>? options = null, bool useDefaultNamespace = true)
    {
        var code = $$"""
                     {{(useDefaultNamespace ? "using TestAssets;" : "")}}
                     public static class Probe
                     {
                         public static object Run() => {{expression}};
                         public static bool Throws<TException>(System.Action action) where TException : System.Exception
                         {
                             try { action(); return false; }
                             catch (TException) { return true; }
                         }
                     }
                     """;
        var driver = Driver(manifest, options).RunGeneratorsAndUpdateCompilation(Compilation(code), out var compilation, out var diagnostics);
        Assert.Empty(diagnostics);
        Assert.Single(driver.GetRunResult().GeneratedTrees);
        using var compiled = new CompiledTestAssembly(compilation);
        return (T)compiled.Assembly.GetType("Probe")!.GetMethod("Run", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, null)!;
    }

    private static CSharpCompilation Compilation(string code = "") => CSharpCompilation.Create("ViteTests_" + Guid.NewGuid().ToString("N"),
        [CSharpSyntaxTree.ParseText(code, ParseOptions)], References, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

    private static GeneratorDriver Driver(string? manifest, Dictionary<string, string>? options = null) => Driver(new ManifestFile(manifest), options);

    private static GeneratorDriver Driver(ManifestFile file, Dictionary<string, string>? options = null) => CSharpGeneratorDriver.Create(
        [new ViteGenerator().AsSourceGenerator()], [file], ParseOptions, new ViteOptions(options), new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, true));

    private sealed class ManifestFile(string? text) : AdditionalText
    {
        public override string Path => ManifestPath;

        public override SourceText? GetText(CancellationToken cancellationToken = default) => text is null ? null : SourceText.From(text);
    }

    private sealed class ViteOptions(Dictionary<string, string>? overrides) : AnalyzerConfigOptionsProvider
    {
        public override AnalyzerConfigOptions GlobalOptions { get; } = new OptionsMap(overrides);

        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => GlobalOptions;

        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => GlobalOptions;
    }

    private sealed class OptionsMap(Dictionary<string, string>? overrides) : AnalyzerConfigOptions
    {
        public override bool TryGetValue(string key, out string value)
        {
            var name = key.Replace("build_property.", "");
            if (overrides is not null && overrides.TryGetValue(name, out value!))
            {
                return true;
            }

            value = name switch
            {
                "ViteManifest"  => ManifestPath,
                "ViteNamespace" => "TestAssets",
                "ViteClassName" => "Assets",
                "ViteBaseUrl"   => "/",
                _               => "",
            };
            return value.Length != 0;
        }
    }
}
