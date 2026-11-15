#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using System.Text.Json;
using Serval.Savanna.Compiler.Analysis;
using Serval.Savanna.Compiler.CodeGeneration;
using Serval.Savanna.Compiler.Parsing;
using Serval.Savanna.Compiler.Syntax;
using Serval.Savanna.LanguageServer.Features;
using Serval.Savanna.LanguageServer.Rpc;
using Serval.Savanna.LanguageServer.Workspace;

namespace Serval.Savanna.LanguageServer.Tests;

public sealed class EditorFeaturesTests
{
    [Theory]
    [InlineData("<#using namespace=\"Example\">", "_Imports.svn")]
    [InlineData("{% using Example %}", "_Imports.svn")]
    [InlineData("<#using namespace=\"Example\"><#model type=\"Page\">{{ Model.Title }}", "Index.svn")]
    [InlineData("{% using Example %}{% model Page %}{{ Model.Title }}", "Index.svn")]
    [InlineData("<#using namespace=\"System\">", "_Imports.svn")]
    [InlineData("<#using namespace=\"Serval.Savanna\">", "_Imports.svn")]
    public async Task ExplicitNamespaceImportsDoNotDuplicateGeneratedUsings(string template, string name)
    {
        using var workspace = new AdhocWorkspace();
        var       snapshot  = Snapshot(workspace, template, name);
        var       features  = new CSharpFeatures(snapshot);
        Assert.Empty(await features.DiagnosticsAsync(CancellationToken.None));

        var namespaceName = snapshot.Document.Usings.Single();
        var offset        = template.IndexOf(namespaceName, StringComparison.Ordinal);
        var hover         = JsonSerializer.SerializeToElement(await features.HoverAsync(offset + 1, CancellationToken.None));
        Assert.Contains("namespace", hover.GetProperty("contents").GetProperty("value").GetString());
        Assert.Equal(offset, Position.Read(hover.GetProperty("range").GetProperty("start")).Offset(snapshot.Document.Text));
    }

    [Theory]
    [InlineData("")]
    [InlineData("<#using namespace=\"Example\">")]
    public async Task InheritedNamespaceImportsResolveWithoutDuplicatingLocalDeclarations(string declaration)
    {
        using var workspace = new AdhocWorkspace();
        var       snapshot  = Snapshot(workspace, declaration + "<#model type=\"Page\">{{ Model.Title }}");
        var       imports   = Parse("_Imports.svn", "<#using namespace=\"Example\"><#using namespace=\"System\">");
        var       documents = SavannaImports.Apply([imports, snapshot.Document]);
        var       features  = new CSharpFeatures(snapshot with { Document = documents.Single(document => !document.IsImports), Templates = documents });
        Assert.Empty(await features.DiagnosticsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task InvalidNamespaceImportsStillReportTheMappedDiagnostic()
    {
        using var    workspace   = new AdhocWorkspace();
        const string template    = "<#using namespace=\"MissingNamespace\">";
        var          snapshot    = Snapshot(workspace, template, "_Imports.svn");
        var          diagnostics = JsonSerializer.SerializeToElement(await new CSharpFeatures(snapshot).DiagnosticsAsync(CancellationToken.None));
        var          diagnostic  = Assert.Single(diagnostics.EnumerateArray());
        Assert.Equal("CS0246", diagnostic.GetProperty("code").GetString());
        var range = diagnostic.GetProperty("range");
        var start = Position.Read(range.GetProperty("start")).Offset(snapshot.Document.Text);
        var end   = Position.Read(range.GetProperty("end")).Offset(snapshot.Document.Text);
        Assert.Equal("MissingNamespace", template[start..end]);
    }

    [Theory]
    [InlineData("<#model type=\"Example.Page\" />\n{{ Model.$", "Title")]
    [InlineData("<#model type=\"Example.Page\" /><#foreach item=\"item\" in=\"Model.Items\">{{ item.$ }}</#foreach>", "Name")]
    [InlineData("<#model type=\"Example.Page\" /><#let name=\"item\" value=\"Model.Items[0]\" />{{ item.$ }}", "Name")]
    [InlineData("<#model type=\"Example.Page\" /><#if condition=\"Model.$", "Title")]
    [InlineData("<#model type=\"Example.Page\"><#unless condition=\"Model.$", "Title")]
    [InlineData("<#model type=\"Example.Page\"><#release>{{ Model.$", "Title")]
    [InlineData("<#model type=\"Example.Page\"><#switch value=\"Model.$", "Title")]
    [InlineData("<#model type=\"Example.Page\"><#switch value=\"(Model).$", "Title")]
    [InlineData("<#model type=\"Example.Page\"><#switch value=\"Model\"><#case value=\"Example.Page page\">{{ page.$ }}<#break></#switch>", "Title")]
    [InlineData("<#model type=\"Example.Page\"><#foreach item=\"item\" in=\"Model.Items\"><#continue if=\"item.$", "Name")]
    [InlineData("<#model type=\"Example.Page\"><#foreach item=\"item\" in=\"Model.Items\"><#break if=\"item.$", "Name")]
    [InlineData("<#model type=\"Example.Page\"><#foreach item=\"item\" in=\"Model.Items\"><#continue if=\"$\">", "item")]
    [InlineData("<#model type=\"Example.Page\"><#foreach item=\"item\" in=\"Model.Items\"><#break if=\"$\">", "item")]
    [InlineData("<#model type=\"Example.$", "Page")]
    [InlineData("{% model Example.Page %}\n{{ Model.$", "Title")]
    [InlineData("{% model Example.Page %}\n{{ Model.$ }}", "Items")]
    [InlineData("{% model Example.Page %}\n{% foreach (var item in Model.Items) %}{{ item.$ }}{% endforeach %}", "Name")]
    [InlineData("{% model Example.Page %}\n{% block body %}{{ Model.$", "Title")]
    [InlineData("{% model Example.Page %}\n{% if (Model.$", "Title")]
    [InlineData("{% model Example.$", "Page")]
    [InlineData("{% using\tExample %}\n{% model Page %}\n{{ Model.$", "Title")]
    public async Task CompletesCSharpInIncompleteTemplatesAndLoopScopes(string template, string expected)
    {
        using var workspace = new AdhocWorkspace();
        var       offset    = template.IndexOf('$');
        var       snapshot  = Snapshot(workspace, template.Replace("$", ""));
        var       result    = JsonSerializer.SerializeToElement(await new CSharpFeatures(snapshot).CompleteAsync(offset, CancellationToken.None));
        Assert.Contains(result.EnumerateArray(), item => item.GetProperty("label").GetString() == expected);
        Assert.All(result.EnumerateArray(), item => Assert.Equal(snapshot.Document.Text.Lines.GetLinePosition(offset).Line, item.GetProperty("textEdit").GetProperty("range").GetProperty("start").GetProperty("line").GetInt32()));
    }

    [Fact]
    public async Task ModelHoverHidesTheEditorProjectionAndNavigatesToTheModelType()
    {
        using var workspace   = new AdhocWorkspace();
        var       snapshot    = Snapshot(workspace, "{% model Example.Page %}\n{{ Model.Title }}");
        var       features    = new CSharpFeatures(snapshot);
        var       offset      = snapshot.Document.Text.ToString().IndexOf("Model", StringComparison.Ordinal) + 1;
        var       hover       = JsonSerializer.SerializeToElement(await features.HoverAsync(offset, CancellationToken.None));
        var       description = hover.GetProperty("contents").GetProperty("value").GetString()!;
        Assert.Contains("Page Model", description);
        Assert.DoesNotContain("__SavannaEditorProjection", description);
        var definition = JsonSerializer.SerializeToElement(await features.DefinitionAsync(offset, CancellationToken.None));
        Assert.EndsWith("Models.cs", definition.GetProperty("uri").GetString());
    }

    [Fact]
    public async Task MetadataMethodsNavigateToTheirDeclarationSignatures()
    {
        using var    workspace  = new AdhocWorkspace();
        const string template   = "{% model decimal %}{{ Model.ToString(\"0.00\", Context.FormatProvider) }}";
        var          features   = new CSharpFeatures(Snapshot(workspace, template));
        var          definition = JsonSerializer.SerializeToElement(await features.DefinitionAsync(template.IndexOf("ToString", StringComparison.Ordinal) + 2, CancellationToken.None));
        var          source     = SourceText.From(await File.ReadAllTextAsync(new Uri(definition.GetProperty("uri").GetString()!).LocalPath));
        var          range      = definition.GetProperty("range");
        var          start      = Position.Read(range.GetProperty("start")).Offset(source);
        var          end        = Position.Read(range.GetProperty("end")).Offset(source);
        Assert.Equal("ToString", source.ToString(TextSpan.FromBounds(start, end)));
        Assert.Contains("IFormatProvider", source.Lines.GetLineFromPosition(start).ToString());
    }

    [Fact]
    public async Task SemanticColorsCoverAttributesTypesPropertiesMethodsKeywordsAndLiterals()
    {
        using var    workspace  = new AdhocWorkspace();
        const string template   = "{% model Example.Page %}\r\n😀 <b title=\"{{ Model.Title }}\">{% if Model.Items.Length > 0 %}{{ Model.Title.ToUpper() }}{% endif %}</b>{% import \"Card.svn\" as Card %}";
        var          snapshot   = Snapshot(workspace, template);
        var          classified = await new CSharpFeatures(snapshot).ClassifyAsync(CancellationToken.None);
        var          encoded    = JsonSerializer.SerializeToElement(SemanticTokens.Encode(snapshot.Document, classified));
        var          data       = encoded.GetProperty("data").EnumerateArray().Select(value => value.GetInt32()).ToArray();
        var          tokens     = new List<(string Text, string Type)>();
        var          line       = 0;
        var          column     = 0;
        for (var index = 0; index < data.Length; index += 5)
        {
            line   += data[index];
            column =  data[index] == 0 ? column + data[index + 1] : data[index + 1];
            tokens.Add((snapshot.Document.Text.ToString(new TextSpan(snapshot.Document.Text.Lines[line].Start + column, data[index + 2])), SemanticTokens.Types[data[index + 3]]));
        }

        Assert.Contains(("Page", "class"), tokens);
        Assert.Contains(("Title", "property"), tokens);
        Assert.Contains(("Model", "variable"), tokens);
        Assert.Contains(("ToUpper", "method"), tokens);
        Assert.Contains(("if", "keyword"), tokens);
        Assert.Contains(("0", "number"), tokens);
        Assert.Contains(("\"Card.svn\"", "string"), tokens);
        Assert.Contains(("as", "keyword"), tokens);
        Assert.DoesNotContain(tokens, token => token.Text.Contains("<b", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HoverAndDiagnosticsMapBackToUtf16TemplatePositions()
    {
        using var workspace = new AdhocWorkspace();
        var       snapshot  = Snapshot(workspace, "{% model Example.Page %}\r\n😀 {{ Model.Title }} {{ Model.Missing }}");
        var       features  = new CSharpFeatures(snapshot);
        var       hover     = JsonSerializer.SerializeToElement(await features.HoverAsync(snapshot.Document.Text.ToString().IndexOf("Title", StringComparison.Ordinal) + 1, CancellationToken.None));
        Assert.Contains("Title", hover.GetProperty("contents").GetProperty("value").GetString());
        var diagnostics = JsonSerializer.SerializeToElement(await features.DiagnosticsAsync(CancellationToken.None));
        var missing     = Assert.Single(diagnostics.EnumerateArray(), item => item.GetProperty("code").GetString() == "CS1061");
        Assert.Equal(1, missing.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32());
        Assert.Equal(30, missing.GetProperty("range").GetProperty("start").GetProperty("character").GetInt32());
    }

    [Fact]
    public async Task DoesNotOfferLoopVariablesOutsideTheirScope()
    {
        using var    workspace   = new AdhocWorkspace();
        const string template    = "{% model Example.Page %}\n{% foreach (var item in Model.Items) %}{{ item.Name }}{% endforeach %}\n{{ item.Name }}";
        var          diagnostics = JsonSerializer.SerializeToElement(await new CSharpFeatures(Snapshot(workspace, template)).DiagnosticsAsync(CancellationToken.None));
        var          missing     = Assert.Single(diagnostics.EnumerateArray(), item => item.GetProperty("code").GetString() == "CS0103");
        Assert.Equal(2, missing.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32());
    }

    [Fact]
    public async Task LineCommentsCannotConsumeProjectionPunctuation()
    {
        using var workspace = new AdhocWorkspace();
        var       snapshot  = Snapshot(workspace, "{% model Example.Page // model\n %}\n{{ Model.Title // value\n }}");
        Assert.Empty(await new CSharpFeatures(snapshot).DiagnosticsAsync(CancellationToken.None));
    }

    [Fact]
    public void CompletesPathsAndInheritedBlocksAndNavigatesAliases()
    {
        var parent   = Parse("Layouts/Site.svn", "{% model string %}{% layout %}{% block body required %}{% endblock %}");
        var child    = Parse("Index.svn", "{% model string %}{% extends \"Layouts/Site.svn\" with Model %}{% block bo");
        var snapshot = new TemplateSnapshot(child, [parent, child], null);
        var blocks   = JsonSerializer.SerializeToElement(TemplateFeatures.Complete(snapshot, child.Text.Length));
        Assert.Equal("body", Assert.Single(blocks.EnumerateArray()).GetProperty("label").GetString());
        var path  = Parse("Index.svn", "{% model string %}{% extends \"Layouts/");
        var paths = JsonSerializer.SerializeToElement(TemplateFeatures.Complete(new TemplateSnapshot(path, [parent, path], null), path.Text.Length));
        Assert.Equal("Layouts/Site.svn", Assert.Single(paths.EnumerateArray()).GetProperty("label").GetString());

        var alias  = Parse("Index.svn", "{% model string %}{% import \"Layouts/Site.svn\" as Site %}{% include Site with Model %}");
        var target = JsonSerializer.SerializeToElement(TemplateFeatures.Definition(new TemplateSnapshot(alias, [parent, alias], null), alias.Text.ToString().LastIndexOf("Site", StringComparison.Ordinal) + 1));
        Assert.Equal(new Uri(parent.Path).AbsoluteUri, target.GetProperty("uri").GetString());
    }

    [Theory]
    [InlineData("{% model string %}{# {% inc #}")]
    [InlineData("{% model string %}{% verbatim %}{% inc{% endverbatim %}")]
    public void DoesNotCompleteDirectivesInsideCommentsOrVerbatim(string source)
    {
        var document = Parse("Index.svn", source);
        Assert.Null(TemplateFeatures.Complete(new TemplateSnapshot(document, [document], null), source.IndexOf("inc", StringComparison.Ordinal) + 3));
    }

    [Fact]
    public async Task CachedCSharpFeaturesRefreshAfterTemplateAndProjectEdits()
    {
        using var projectWorkspace = new AdhocWorkspace();
        using var templates        = new SavannaWorkspace();
        var       snapshot         = Snapshot(projectWorkspace, "{% model Example.Page %}{{ Model.Title }}");
        var       original         = templates.GetCSharpFeatures(snapshot)!;
        Assert.Same(original, templates.GetCSharpFeatures(snapshot));
        Assert.Empty(await original.DiagnosticsAsync(CancellationToken.None));

        var edited         = Parse("Index.svn", "{% model Example.Page %}{{ Model.Missing }}");
        var editedSnapshot = snapshot with { Document = edited, Templates = [edited] };
        var changed        = templates.GetCSharpFeatures(editedSnapshot)!;
        Assert.NotSame(original, changed);
        Assert.NotEmpty(await changed.DiagnosticsAsync(CancellationToken.None));

        var projectEdited = editedSnapshot with { Project = snapshot.Project!.AddDocument("New.cs", "namespace Example; public class NewType {}").Project };
        Assert.NotSame(changed, templates.GetCSharpFeatures(projectEdited));
        templates.InvalidateProjects();
        Assert.NotSame(changed, templates.GetCSharpFeatures(editedSnapshot));
    }

    [Fact]
    public void ProjectionPreservesEveryMappedCharacter()
    {
        var document   = Parse("Index.svn", "{% model Example.Page %}\r\n{% if (Model.Title == \"}}\") %}😀 {{ Model.Title }}{% endif %}");
        var projection = SavannaProjection.Create(document);
        Assert.All(projection.Mappings, mapping => Assert.Equal(document.Text.ToString(mapping.SourceSpan), projection.Text.Substring(mapping.GeneratedSpan.Start, mapping.GeneratedSpan.Length)));
    }

    [Fact]
    public void MarkupPathsAndCSharpProjectionUseExactAttributeSpans()
    {
        var card  = Parse("Card.svn", "<#model type=\"string\" />{{ Model }}");
        var page  = Parse("Index.svn", "<#model type=\"string\" /><#include view=\"Ca");
        var items = JsonSerializer.SerializeToElement(TemplateFeatures.Complete(new TemplateSnapshot(page, [page, card], null), page.Text.Length));
        Assert.Contains(items.EnumerateArray(), item => item.GetProperty("label").GetString() == "Card.svn");

        page = Parse("Index.svn", "<#model type=\"string\" /><#include view=\"Card.svn\" model=\"Model\" />");
        var offset = page.Text.ToString().IndexOf("Card.svn", StringComparison.Ordinal) + 1;
        var target = JsonSerializer.SerializeToElement(TemplateFeatures.Definition(new TemplateSnapshot(page, [page, card], null), offset));
        Assert.Equal(new Uri(card.Path).AbsoluteUri, target.GetProperty("uri").GetString());

        var projection = SavannaProjection.Create(Parse("Index.svn", "<#model type=\"Example.Page\" /><#let name=\"title\" value=\"Model.Title\" /><#foreach item=\"item\" in=\"Model.Items\">{{ item.Name }}</#foreach>"));
        Assert.All(projection.Mappings, mapping => Assert.Equal(mapping.SourceSpan.Length, mapping.GeneratedSpan.Length));
    }

    [Theory]
    [InlineData(true, "DebugMissing")]
    [InlineData(false, "ReleaseMissing")]
    public async Task BuildGuardsUseTheProjectsDebugSymbolForMappedDiagnostics(bool debug, string expected)
    {
        using var workspace = new AdhocWorkspace();
        const string template = """
                                <#model type="Example.Page">
                                <#debug><#unless condition="Model.Items.Length > 0">{{ Model.DebugMissing }}</#unless></#debug>
                                <#release><#unless condition="Model.Items.Length > 0">{{ Model.ReleaseMissing }}</#unless></#release>
                                """;
        var snapshot = Snapshot(workspace, template);
        snapshot = snapshot with { Project = snapshot.Project!.WithParseOptions(new CSharpParseOptions(LanguageVersion.Preview, preprocessorSymbols: debug ? ["DEBUG"] : [])) };
        var diagnostics = JsonSerializer.SerializeToElement(await new CSharpFeatures(snapshot).DiagnosticsAsync(CancellationToken.None));
        var diagnostic  = Assert.Single(diagnostics.EnumerateArray());
        Assert.Equal("CS1061", diagnostic.GetProperty("code").GetString());
        var range = diagnostic.GetProperty("range");
        var start = Position.Read(range.GetProperty("start")).Offset(snapshot.Document.Text);
        var end   = Position.Read(range.GetProperty("end")).Offset(snapshot.Document.Text);
        Assert.Equal(expected, template[start..end]);
        var projection = SavannaProjection.Create(snapshot.Document);
        Assert.All(projection.Mappings, mapping => Assert.Equal(template.Substring(mapping.SourceSpan.Start, mapping.SourceSpan.Length),
            projection.Text.Substring(mapping.GeneratedSpan.Start, mapping.GeneratedSpan.Length)));
    }

    [Theory]
    [InlineData("debug", "DEBUG")]
    [InlineData("release", "not defined")]
    [InlineData("unless", "false")]
    [InlineData("switch", "C# switch")]
    [InlineData("case", "pattern")]
    [InlineData("break", "optional if")]
    [InlineData("continue", "optional if")]
    public void ControlTagsHaveCompletionAndHoverDescriptions(string name, string description)
    {
        var document = Parse("Index.svn", "<#" + name + ">");
        var snapshot = new TemplateSnapshot(document, [document], null);
        var items    = JsonSerializer.SerializeToElement(TemplateFeatures.Complete(snapshot, 3));
        Assert.Contains(items.EnumerateArray(), item => item.GetProperty("label").GetString() == name);
        var hover = JsonSerializer.SerializeToElement(TemplateFeatures.Hover(snapshot, 3));
        Assert.Contains(description, hover.GetProperty("contents").GetProperty("value").GetString());
    }

    private static TemplateSnapshot Snapshot(AdhocWorkspace workspace, string template, string name = "Index.svn")
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
                                                                                     .Append(typeof(SavannaRenderContext).Assembly.Location).Distinct().Select(path => MetadataReference.CreateFromFile(path));
        var project = workspace.AddProject(ProjectInfo.Create(ProjectId.CreateNewId(), VersionStamp.Create(), "EditorTest", "EditorTest", LanguageNames.CSharp,
            parseOptions: new CSharpParseOptions(LanguageVersion.Preview), compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary), metadataReferences: references));
        const string models = "namespace Example; public record Item(string Name); public record Page(string Title, Item[] Items);";
        var          path   = Path.Combine(Path.GetTempPath(), "SavannaEditorTests", "Models.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, models);
        project = project.AddDocument("Models.cs", models, filePath: path).Project;
        var document = Parse(name, template);
        return new TemplateSnapshot(document, [document], project);
    }

    private static SavannaDocument Parse(string name, string source) => SavannaParser.Parse(Path.Combine(Path.GetTempPath(), "SavannaEditorTests", name), name, SourceText.From(source));
}
