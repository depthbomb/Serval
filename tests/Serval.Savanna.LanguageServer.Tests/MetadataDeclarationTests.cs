#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System.Text.Json;
using Serval.Savanna.LanguageServer.Features;

namespace Serval.Savanna.LanguageServer.Tests;

public sealed class MetadataDeclarationTests
{
    [Fact]
    public async Task RegeneratesAnIncompleteMetadataFileWithoutLeavingTemporaryFiles()
    {
        var name       = "SavannaMetadata" + Guid.NewGuid().ToString("N");
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator).Select(path => MetadataReference.CreateFromFile(path)).ToArray();
        var compiled = CSharpCompilation.Create(name, [CSharpSyntaxTree.ParseText("public class " + name + " { public void Run() {} }")],
            references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var image = new MemoryStream();
        Assert.True(compiled.Emit(image).Success);
        var consumer = CSharpCompilation.Create("Consumer", references: references.Append(MetadataReference.CreateFromImage(image.ToArray())));
        var symbol   = consumer.GetTypeByMetadataName(name)!.GetMembers("Run").Single();
        var target   = JsonSerializer.SerializeToElement(await MetadataDeclaration.CreateAsync(symbol, CancellationToken.None));
        var path     = new Uri(target.GetProperty("uri").GetString()!).LocalPath;
        var expected = await File.ReadAllTextAsync(path);
        await File.WriteAllTextAsync(path, "// interrupted write");
        await MetadataDeclaration.CreateAsync(symbol, CancellationToken.None);
        Assert.Equal(expected, await File.ReadAllTextAsync(path));
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "*.tmp"));
    }
}
