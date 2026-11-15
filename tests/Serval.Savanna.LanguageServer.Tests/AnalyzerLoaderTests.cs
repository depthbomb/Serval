#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Serval.Savanna.LanguageServer.Workspace;

namespace Serval.Savanna.LanguageServer.Tests;

public sealed class AnalyzerLoaderTests
{
    [Fact]
    public void EditorGeneratorsLeaveBuildOutputsWritable()
    {
        var directory = Path.Combine(Path.GetTempPath(), "SavannaAnalyzerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "EditorGenerator.dll");
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
                                                                                     .Select(file => MetadataReference.CreateFromFile(file));
        var compilation = CSharpCompilation.Create("EditorGenerator", [
            CSharpSyntaxTree.ParseText("""
                                       using Microsoft.CodeAnalysis;
                                       [Generator]
                                       public sealed class ExampleGenerator : ISourceGenerator
                                       {
                                           public void Initialize(GeneratorInitializationContext context) {}
                                           public void Execute(GeneratorExecutionContext context) => context.AddSource("Output.cs", "public class Generated {}");
                                       }
                                       """),
        ], references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Assert.True(compilation.Emit(path).Success);
        using var loader = new EditorAnalyzerLoader();
        loader.AddDependencyLocation(path);
        var generator = Assert.Single(new AnalyzerFileReference(path, loader).GetGenerators(LanguageNames.CSharp));
        var driver    = CSharpGeneratorDriver.Create(generator).RunGenerators(CSharpCompilation.Create("Target"));
        Assert.Single(driver.GetRunResult().GeneratedTrees);

        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.True(exclusive.CanWrite);
        GC.KeepAlive(driver);
    }
}
