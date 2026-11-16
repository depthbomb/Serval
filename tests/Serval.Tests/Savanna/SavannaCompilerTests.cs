#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using System.Reflection;
using System.Runtime.Loader;
using Serval.Savanna;
using Serval.Savanna.Compiler;
using Serval.Savanna.Compiler.Parsing;
using Serval.Savanna.Generators;

namespace Serval.Tests.Savanna;

public sealed partial class SavannaCompilerTests
{
    private static readonly CSharpParseOptions Options = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.CSharp15);
    private static readonly MetadataReference[] References = [.. ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
                                                                                                                         .Append(typeof(SavannaView<>).Assembly.Location).Append(typeof(RequestContext).Assembly.Location).Distinct()
                                                                                                                         .Select<string, MetadataReference>(path => MetadataReference.CreateFromFile(path))];

    [Fact]
    public async Task ThreeLevelsPreserveModelsImportsAndParentOrder()
    {
        var result = Compile(new Dictionary<string, string>
        {
            ["Base.svn"]   = "{% using BaseModels %}{% model Page %}{% layout %}<title>{% block title %}{{ Model.Text }}{% endblock %}</title>{% block body required %}{% endblock %}",
            ["Middle.svn"] = "{% using MiddleModels %}{% model Page %}{% layout %}{% extends \"Base.svn\" with Model.Base %}{% block title %}{% parent %}/{{ Model.Text }}{% endblock %}",
            ["Page.svn"]   = "{% using ChildModels %}{% model Page %}{% extends \"Middle.svn\" with Model.Middle %}{% import \"Card.svn\" as Card %}{% block title %}{% parent %}/{{ Model.Text }}{% endblock %}{% block body %}{% foreach (var item in Model.Items) %}{% include Card with item %}{% endforeach %}{% endblock %}",
            ["Card.svn"]   = "{% model string %}<b>{{ Model }}</b>",
        });
        Assert.Empty(result.Diagnostics);
        Assert.Equal(2, result.Sources.Count);
        var output = await Run(result, """
                                       namespace BaseModels { public sealed record Page(string Text); }
                                       namespace MiddleModels { public sealed record Page(string Text, BaseModels.Page Base); }
                                       namespace ChildModels { public sealed record Page(string Text, MiddleModels.Page Middle, string[] Items); }
                                       public static class Entry
                                       {
                                           public static async System.Threading.Tasks.Task<string> Run()
                                               => await TestViews.Views.Page.RenderToStringAsync(new ChildModels.Page("child", new("middle", new("base")), ["<&", "two"]));
                                       }
                                       """);
        Assert.Equal("<!-- Savanna view: Page.svn --><!-- Savanna layout: Middle.svn --><!-- Savanna layout: Base.svn --><title>base/middle/child</title><!-- Savanna partial: Card.svn --><b>&lt;&amp;</b><!-- Savanna partial: Card.svn --><b>two</b>", output);
    }

    [Theory]
    [InlineData("<span>one</span><span>two</span>", "<span>one</span><span>two</span>")]
    [InlineData("\r\n\t<span>one</span> <span>two</span>\n<pre>  a\r\n\tb</pre>", " <span>one</span> <span>two</span><pre>  a\r\n\tb</pre>")]
    [InlineData("<!-- authored comment --><textarea>\n  keep\tthis\n</textarea>", "<!-- authored comment --><textarea>\n  keep\tthis\n</textarea>")]
    [InlineData("<div\n class = \"card\" id='one' title=\"two words\">\n  hello\n  world\n</div>", "<div class=card id=one title=\"two words\">hello world</div>")]
    [InlineData("<input value=\"a&#32;b\"/><input value=\"\" /><a href=\"/?a=b\">x</a>", "<input value=a&#32;b /><input value=\"\" /><a href=\"/?a=b\">x</a>")]
    [InlineData("<b>one</b>\n<i>two</i>", "<b>one</b> <i>two</i>")]
    [InlineData("<script>\n// comment\nalert('x');\n</script>\n<style>\np { white-space: pre; }\n</style>", "<script>\n// comment\nalert('x');\n</script> <style>\np { white-space: pre; }\n</style>")]
    [InlineData("<span>one</span> <script>/* hidden */</script> <span>two</span>", "<span>one</span> <script>/* hidden */</script> <span>two</span>")]
    [InlineData("<div data-savanna-preserve-whitespace>\n <div class=\"nested\">\n x\t y\n</div>\n</div>\n<b class=\"after\">z</b>", "<div data-savanna-preserve-whitespace>\n <div class=\"nested\">\n x\t y\n</div>\n</div><b class=after>z</b>")]
    [InlineData("<svg><text> a\n b </text></svg>\n<span class=\"outside\">c</span>", "<svg><text> a\n b </text></svg> <span class=outside>c</span>")]
    [InlineData("<div title=\"one\r\ntwo\tthree\" data-label=\"a b\nc\"></div>", "<div title=one&#10;two&#9;three data-label=\"a b&#10;c\"></div>")]
    [InlineData("<svg/>\n<b class=\"outside\">x</b>", "<svg/> <b class=outside>x</b>")]
    [InlineData("<script><!--<script>\n// nested\n</script>\n// still script\n--></script>\n<div>x</div>", "<script><!--<script>\n// nested\n</script>\n// still script\n--></script><div>x</div>")]
    public async Task GeneratedViewsMinifyHtmlAndPreserveSensitiveWhitespace(string html, string expected)
    {
        var result = Compile(new Dictionary<string, string> { ["Page.svn"] = "{% model int %}" + html });
        var output = await Run(result, """
                                       public static class Entry
                                       {
                                           public static async System.Threading.Tasks.Task<string> Run() => await TestViews.Views.Page.RenderToStringAsync(0);
                                       }
                                       """);
        Assert.Equal("<!-- Savanna view: Page.svn -->" + expected, output);
    }

    [Fact]
    public async Task DynamicAttributesRemainQuotedAndEncoded()
    {
        var result = Compile(new Dictionary<string, string> { ["Page.svn"] = "{% model string %}<div class=\"static\"\n title=\"{{ Model }}\" data-empty=\"{{ string.Empty }}\">\ntext\n</div>" });
        var output = await Run(result, """
                                       public static class Entry
                                       {
                                           public static async System.Threading.Tasks.Task<string> Run() => await TestViews.Views.Page.RenderToStringAsync("one two\" onclick=\"bad");
                                       }
                                       """);
        Assert.Equal("<!-- Savanna view: Page.svn --><div class=static title=\"one two&quot; onclick=&quot;bad\" data-empty=\"\">text</div>", output);
    }

    [Fact]
    public async Task ConditionalInlineSpacingSurvivesEveryBranchAndEmptyLoops()
    {
        var result = Compile(new Dictionary<string, string>
        {
            ["Page.svn"] = "{% model bool %}<span>one</span>{% if Model %}<div>block</div>{% else %}<b>inline</b>{% endif %} <i>two</i>{% foreach (var item in System.Array.Empty<int>()) %}<div>{{ item }}</div>{% endforeach %} <em>three</em>",
        });
        var output = await Run(result, """
                                       public static class Entry
                                       {
                                           public static async System.Threading.Tasks.Task<string> Run() => await TestViews.Views.Page.RenderToStringAsync(false);
                                       }
                                       """);
        Assert.Equal("<!-- Savanna view: Page.svn --><span>one</span><b>inline</b> <i>two</i> <em>three</em>", output);
    }

    [Fact]
    public async Task DynamicMarkupAndConditionalTagBoundariesKeepWhitespace()
    {
        var result = Compile(new Dictionary<string, string>
        {
            ["Raw.svn"]    = "{% model int %}{% html Serval.Savanna.SavannaHtml.Trusted(\"<pre>\") %}\n  first\n  second\n</pre>",
            ["Branch.svn"] = "{% model int %}{% if Model > 0 %}<pre>{% else %}<div>{% endif %}\n  text\n{% if Model > 0 %}</pre>{% else %}</div>{% endif %}",
        });
        Assert.Empty(result.Diagnostics);
        var output = await Run(result, """
                                       public static class Entry
                                       {
                                           public static async System.Threading.Tasks.Task<string> Run()
                                               => await TestViews.Views.Raw.RenderToStringAsync(0) + await TestViews.Views.Branch.RenderToStringAsync(1);
                                       }
                                       """);
        Assert.Equal("<!-- Savanna view: Raw.svn --><pre>\n  first\n  second\n</pre><!-- Savanna view: Branch.svn --><pre>\n  text\n</pre>", output);
    }

    [Fact]
    public async Task LayoutsAndPartialsInPreservedRegionsRetainTheirAuthoredText()
    {
        var result = Compile(new Dictionary<string, string>
        {
            ["Base.svn"] = "{% model int %}{% layout %}<pre>{% block body required %}{% endblock %}</pre><div>{% block normal required %}{% endblock %}</div>",
            ["Page.svn"] = "{% model int %}{% extends \"Base.svn\" with Model %}{% block body %}\n  {% include \"Card.svn\" with Model %}\n{% endblock %}{% block normal %}{% include \"Card.svn\" with Model %}{% endblock %}",
            ["Card.svn"] = "{% model int %}<b class=\"card\">\n  line one\n  line two\n</b>",
        });
        var output = await Run(result, """
                                       public static class Entry
                                       {
                                           public static async System.Threading.Tasks.Task<string> Run() => await TestViews.Views.Page.RenderToStringAsync(0);
                                       }
                                       """);
        Assert.Equal("<!-- Savanna view: Page.svn --><!-- Savanna layout: Base.svn --><pre>\n  <!-- Savanna partial: Card.svn --><b class=\"card\">\n  line one\n  line two\n</b>\n</pre><div><!-- Savanna partial: Card.svn --><b class=card> line one line two </b></div>", output);
    }

    [Theory]
    [InlineData("{{ new { Text = \"}}\" }.Text }}", "}}")]
    [InlineData("{{ $\"{new { Text = \"}}\" }.Text}\" }}", "}}")]
    [InlineData("{{ \"\"\"}} %} raw\"\"\" }}", "}} %} raw")]
    [InlineData("{{ 1 /* }} */ + 2 }}", "3")]
    [InlineData("{% if Model > 0 %}yes{% elseif Model == 0 %}zero{% else %}no{% endif %}", "yes")]
    [InlineData("{% for (var i = 0; i < Model; i++) %}{{ i }}{% endfor %}", "012")]
    [InlineData("{% verbatim %}{{ client.value }}{% endverbatim %}", "{{ client.value }}")]
    [InlineData("{# comment #}before{# another #}after", "beforeafter")]
    [InlineData("<script>const x = 1;</scr{# comment #}ipt><p>{{ Model }}</p>", "<script>const x = 1;</script><p>3</p>")]
    [InlineData("<style>p { color: red; }</sty<#-- comment -->le><p>{{ Model }}</p>", "<style>p { color: red; }</style><p>3</p>")]
    [InlineData("{{ Model // comment\n }}", "3")]
    [InlineData("<#if condition='Model > 0 // comment'>yes</#if>", "yes")]
    [InlineData("<#let name='value' value='Model // comment'>{{ value }}", "3")]
    [InlineData("<#foreach item='item' in='new[] { Model } // comment'>{{ item }}</#foreach>", "3")]
    public async Task CSharpLexingAndControlFlowPreserveMeaning(string body, string expected)
    {
        var result = Compile(new Dictionary<string, string> { ["Page.svn"] = "{% model int %}" + body });
        Assert.Empty(result.Diagnostics);
        var output = await Run(result, """
                                       public static class Entry
                                       {
                                           public static async System.Threading.Tasks.Task<string> Run() => await TestViews.Views.Page.RenderToStringAsync(3);
                                       }
                                       """);
        Assert.Equal("<!-- Savanna view: Page.svn -->" + expected, output);
    }

    [Fact]
    public async Task AsyncEnumerationUsesTheRenderCancellationContext()
    {
        var result = Compile(new Dictionary<string, string>
        {
            ["Page.svn"] = "{% model System.Collections.Generic.IAsyncEnumerable<int> %}{% await foreach (var item in Model.WithCancellation(Context.CancellationToken)) %}{{ item }}{% endforeach %}",
        });
        var output = await Run(result, """
                                       public static class Entry
                                       {
                                           private static async System.Collections.Generic.IAsyncEnumerable<int> Items()
                                           {
                                               await System.Threading.Tasks.Task.Yield();
                                               yield return 1;
                                               yield return 2;
                                           }
                                           public static async System.Threading.Tasks.Task<string> Run() => await TestViews.Views.Page.RenderToStringAsync(Items());
                                       }
                                       """);
        Assert.Equal("<!-- Savanna view: Page.svn -->12", output);
    }

    [Theory]
    [InlineData("{% model int %}{% if true %}text", "SAVANNA001")]
    [InlineData("{% model int %}{{ Model", "SAVANNA001")]
    [InlineData("{% model int %}{% include \"Missing.svn\" with Model %}", "SAVANNA002")]
    [InlineData("{% model int %}{% include \"Page.svn\" with Model %}", "SAVANNA002")]
    [InlineData("{% model int %}<div title={{ Model }}></div>", "SAVANNA003")]
    [InlineData("{% model int %}<script>{{ Model }}</script>", "SAVANNA003")]
    [InlineData("{% model int %}<script>// </script\u00a0>\n{{ Model }}</script>", "SAVANNA003")]
    [InlineData("{% model int %}<script><!--<script></script>{{ Model }}</script>", "SAVANNA003")]
    [InlineData("{% model int %}<style></style\u2003>{{ Model }}</style>", "SAVANNA003")]
    [InlineData("{% model int %}<plaintext></plaintext>{{ Model }}", "SAVANNA003")]
    [InlineData("{% model int %}<style>{{ Model }}</style>", "SAVANNA003")]
    [InlineData("{% model int %}<button onclick=\"{{ Model }}\">x</button>", "SAVANNA003")]
    [InlineData("{% model int %}<a href=\"/prefix/{{ Model }}\">x</a>", "SAVANNA003")]
    [InlineData("{% model int %}<a href=\"{{ Model }}/suffix\">x</a>", "SAVANNA003")]
    [InlineData("{% model int %}<svg><set attributeName=\"href\" to=\"{{ Model }}\" /></svg>", "SAVANNA003")]
    [InlineData("{% model int %}<iframe srcdoc=\"{{ Model }}\"></iframe>", "SAVANNA003")]
    [InlineData("{% model int %}{% if true %}<a title=\"{% endif %}x\">", "SAVANNA003")]
    [InlineData("{% model int %}{% parent %}", "SAVANNA001")]
    [InlineData("{% model int %}{% block x %}one{% endblock %}{% block x %}two{% endblock %}", "SAVANNA001")]
    [InlineData("<#model type='int'><!{# comment #}-- <b> <#include view='Card.svn' /> -->", "SAVANNA003")]
    public void InvalidTemplatesHaveSourceLocatedDiagnostics(string template, string code)
    {
        var result = Compile(new Dictionary<string, string> { ["Page.svn"] = template, ["Card.svn"] = "<#model type='int'><p>{{ Model }}</p>" });
        Assert.Empty(result.Sources);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == code && diagnostic.Path == "/Templates/Page.svn");
    }

    [Fact]
    public void GraphValidationRejectsInheritanceCyclesUnknownOverridesAndMissingRequiredBlocks()
    {
        var cycle = Compile(new Dictionary<string, string>
        {
            ["A.svn"] = "{% model int %}{% extends \"B.svn\" with Model %}",
            ["B.svn"] = "{% model int %}{% extends \"A.svn\" with Model %}",
        });
        Assert.Contains(cycle.Diagnostics, diagnostic => diagnostic.Message.Contains("Cyclic"));
        var missing = Compile(new Dictionary<string, string>
        {
            ["Base.svn"] = "{% model int %}{% layout %}{% block body required %}{% endblock %}",
            ["Page.svn"] = "{% model int %}{% extends \"Base.svn\" with Model %}{% block typo %}x{% endblock %}",
        });
        Assert.Contains(missing.Diagnostics, diagnostic => diagnostic.Message.Contains("Unknown inherited"));
        Assert.Contains(missing.Diagnostics, diagnostic => diagnostic.Message.Contains("Required block"));
    }

    [Fact]
    public void GeneratedNamesRejectCollisionsAndPathEscapes()
    {
        var result = Compile(new Dictionary<string, string>
        {
            ["a-b.svn"]           = "{% model int %}",
            ["a_b.svn"]           = "{% model int %}",
            ["../Escape.svn"]     = "{% model int %}",
            ["Folder.svn"]        = "<#model type='int'>",
            ["Folder/Page.svn"]   = "<#model type='int'>",
            ["a_b/Deep/Page.svn"] = "<#model type='int'>",
        });
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Message.Contains("Duplicate"));
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Message.Contains("project-relative"));
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Path == "/Templates/Folder.svn" && diagnostic.Message.Contains("conflicts with a view folder"));
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Path == "/Templates/a-b.svn"    && diagnostic.Message.Contains("conflicts with a view folder"));
        Assert.Empty(result.Sources);
    }

    [Fact]
    public void CSharpErrorsMapBackToTheExpressionAndTypedIncludes()
    {
        const string template = "{% model string %}\r\n<p>🙂 {{ Model.Missing }}</p>";
        var          result   = Compile(new Dictionary<string, string> { ["Page.svn"] = template });
        Assert.Empty(result.Diagnostics);
        var diagnostics = CreateCompilation(result, "").GetDiagnostics();
        var error       = Assert.Single(diagnostics.Where(diagnostic => diagnostic.Id == "CS1061"));
        var span        = error.Location.GetMappedLineSpan();
        Assert.Equal("/Templates/Page.svn", span.Path);
        Assert.Equal(1, span.StartLinePosition.Line);
        Assert.Equal(template.Split('\n')[1].IndexOf("Missing", StringComparison.Ordinal), span.StartLinePosition.Character);
        var source = Assert.Single(result.Sources);
        Assert.Contains(source.Mappings, mapping => template.Substring(mapping.SourceSpan.Start, mapping.SourceSpan.Length)             == "Model.Missing"
                                                    && source.Text.Substring(mapping.GeneratedSpan.Start, mapping.GeneratedSpan.Length) == "Model.Missing");

        var wrongModel = Compile(new Dictionary<string, string>
        {
            ["Page.svn"]   = "{% model string %}{% include \"Number.svn\" with Model %}",
            ["Number.svn"] = "{% model int %}{{ Model }}",
        });
        Assert.Contains(CreateCompilation(wrongModel, "").GetDiagnostics(), diagnostic => diagnostic.Id == "CS1503");
    }

    [Fact]
    public void UrlInterpolationAcceptsOrdinaryStrings()
    {
        var result = Compile(new Dictionary<string, string> { ["Page.svn"] = "{% model string %}<a href=\"{{ Model }}\">x</a>" });
        Assert.DoesNotContain(CreateCompilation(result, "").GetDiagnostics(), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Theory]
    [InlineData("Views")]
    [InlineData("./Views")]
    [InlineData("Unused/../Views/")]
    [InlineData(".\\Views")]
    [InlineData("Unused\\..\\Views\\")]
    public void GeneratorDiscoversFilesAndRemovesDeletedOutputs(string root)
    {
        var             file        = new TemplateFile("/project/Views/Page.svn", "{% model int %}<p>{{ Model }}</p>");
        var             compilation = CSharpCompilation.Create("Generator", references: References, options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        GeneratorDriver driver      = CSharpGeneratorDriver.Create([new SavannaGenerator().AsSourceGenerator()], [file], Options, new SavannaOptions(root));
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var generated, out var diagnostics);
        Assert.Empty(diagnostics);
        Assert.DoesNotContain(generated.GetDiagnostics(), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Single(driver.GetRunResult().GeneratedTrees);
        driver = driver.RemoveAdditionalTexts([file]).RunGenerators(compilation);
        Assert.Empty(driver.GetRunResult().GeneratedTrees);
    }

    [Fact]
    public void EditingALayoutUpdatesItsDescendantsWithoutChangingUnrelatedSources()
    {
        var             layout      = new TemplateFile("/project/Views/Base.svn", "{% model int %}{% layout %}one{% block body %}base{% endblock %}");
        var             page        = new TemplateFile("/project/Views/Page.svn", "{% model int %}{% extends \"Base.svn\" with Model %}{% block body %}child{% endblock %}");
        var             other       = new TemplateFile("/project/Views/Other.svn", "{% model int %}unrelated");
        var             compilation = CSharpCompilation.Create("Incremental", references: References, options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        GeneratorDriver driver      = CSharpGeneratorDriver.Create([new SavannaGenerator().AsSourceGenerator()], [layout, page, other], Options, new SavannaOptions());
        driver = driver.RunGenerators(compilation);
        var before = driver.GetRunResult().GeneratedTrees.Select(tree => tree.ToString()).ToArray();
        driver = driver.ReplaceAdditionalText(layout, new TemplateFile(layout.Path, "{% model int %}{% layout %}two{% block body %}base{% endblock %}")).RunGenerators(compilation);
        var after = driver.GetRunResult().GeneratedTrees.Select(tree => tree.ToString()).ToArray();
        Assert.Equal(2, after.Length);
        Assert.Contains(Assert.Single(before, source => source.Contains("unrelated")), after);
        Assert.Contains(after, source => source.Contains("\"two\"u8"));
        Assert.DoesNotContain(after, source => source.Contains("\"one\"u8"));
    }

    [Fact]
    public async Task ContextualKeywordImportAliasesDoNotConsumeTheWithSeparator()
    {
        var result = Compile(new Dictionary<string, string>
        {
            ["Page.svn"] = "{% model int %}{% import \"Card.svn\" as with %}{% include with with Model %}",
            ["Card.svn"] = "{% model int %}{{ Model }}",
        });
        Assert.Equal("<!-- Savanna view: Page.svn --><!-- Savanna partial: Card.svn -->3", await Run(result, """
                                                                                                         public static class Entry
                                                                                                         {
                                                                                                             public static async System.Threading.Tasks.Task<string> Run() => await TestViews.Views.Page.RenderToStringAsync(3);
                                                                                                         }
                                                                                                         """));
    }

    private static SavannaCompilation Compile(Dictionary<string, string> templates)
        => SavannaCompiler.Compile([.. templates.Select(pair => SavannaParser.Parse("/Templates/" + pair.Key, pair.Key, SourceText.From(pair.Value)))], "TestViews");

    private static CSharpCompilation CreateCompilation(SavannaCompilation result, string code, CSharpParseOptions? options = null)
    {
        Assert.Empty(result.Diagnostics);
        return CSharpCompilation.Create("SavannaTests_" + Guid.NewGuid().ToString("N"),
            result.Sources.Select(source => CSharpSyntaxTree.ParseText(source.Text, options ?? Options, source.HintName)).Append(CSharpSyntaxTree.ParseText(code, options ?? Options)),
            References, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
    }

    private static async Task<string> Run(SavannaCompilation result, string code, CSharpParseOptions? options = null, bool normalizeComments = true)
    {
        var       compilation = CreateCompilation(result, code, options);
        using var stream      = new MemoryStream();
        var       emit        = compilation.Emit(stream);
        Assert.True(emit.Success, string.Join(Environment.NewLine, emit.Diagnostics));
        stream.Position = 0;
        var loadContext = new AssemblyLoadContext(compilation.AssemblyName!, true);
        try
        {
            var assembly = loadContext.LoadFromStream(stream);
            var output   = await (Task<string>)assembly.GetType("Entry")!.GetMethod("Run", BindingFlags.Static | BindingFlags.Public)!.Invoke(null, null)!;
            return normalizeComments ? SavannaTestOutput.NamesOnly(output) : output;
        }
        finally
        {
            loadContext.Unload();
        }
    }

    private sealed class TemplateFile(string path, string text) : AdditionalText
    {
        public override string Path => path;

        public override SourceText GetText(CancellationToken cancellationToken = default) => SourceText.From(text);
    }

    private sealed class SavannaOptions(string root = "Views") : AnalyzerConfigOptionsProvider
    {
        public override AnalyzerConfigOptions GlobalOptions { get; } = new OptionsMap(root);

        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => GlobalOptions;

        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => GlobalOptions;
    }

    private sealed class OptionsMap(string root) : AnalyzerConfigOptions
    {
        public override bool TryGetValue(string key, out string value)
        {
            value = key switch
            {
                "build_property.MSBuildProjectDirectory" => "/project",
                "build_property.SavannaRoot"                => root,
                "build_property.SavannaNamespace"           => "TestViews",
                _                                        => "",
            };
            return value.Length != 0;
        }
    }
}
