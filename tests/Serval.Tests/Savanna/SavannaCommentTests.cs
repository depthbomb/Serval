#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Hosting;
using System.Text;
using System.Text.RegularExpressions;
using Serval.Savanna;
using Serval.Savanna.Compiler;
using Serval.Savanna.Compiler.Parsing;

namespace Serval.Tests.Savanna;

public sealed partial class SavannaCompilerTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CompiledCommentsUseDebugSymbolsForViewsLayoutsPartialsAndComponents(bool debug)
    {
        var result = Compile(new Dictionary<string, string>
        {
            ["Base.svn"] = """<#model type="string"><#layout><main><#block name="Body" required></#block></main>""",
            ["Page.svn"] = """<#model type="string"><#extends view="Base.svn"><#block name="Body"><#for clause="int i = 0; i < 20; i++"><#include view="Card.svn" /><#component type="Example.Badge" arguments="Model" /></#for></#block>""",
            ["Card.svn"] = """<#model type="string"><b>card</b>""",
        });
        var output = await Run(result, """
                                       namespace Example {
                                           public sealed class Badge : Serval.Savanna.ISavannaComponent<string> {
                                               public System.Threading.Tasks.ValueTask RenderAsync(Serval.Savanna.SavannaWriter writer, string arguments, Serval.Savanna.SavannaRenderContext context)
                                                   => writer.WriteLiteralAsync("<i>badge</i>"u8.ToArray(), context.CancellationToken);
                                           }
                                       }
                                       public sealed class Services : System.IServiceProvider {
                                           public object? GetService(System.Type type) => type == typeof(Example.Badge) ? new Example.Badge() : null;
                                       }
                                       public static class Entry {
                                           public static async System.Threading.Tasks.Task<string> Run() {
                                               var services = new Services();
                                               var normal = await TestViews.Views.Page.RenderToStringAsync("private model value", new Serval.Savanna.SavannaRenderContext(services, development: false));
                                               using var body = new System.IO.MemoryStream();
                                               using var writer = new Serval.Savanna.SavannaWriter(async (bytes, token) => {
                                                   await System.Threading.Tasks.Task.Yield();
                                                   await body.WriteAsync(bytes, token);
                                               }, 1024);
                                               await TestViews.Views.Page.RenderAsync(writer, "private model value", new Serval.Savanna.SavannaRenderContext(services, development: true));
                                               await writer.FlushAsync();
                                               var development = System.Text.Encoding.UTF8.GetString(body.ToArray());
                                               return normal + "\n" + development;
                                           }
                                       }
                                       """, Options.WithPreprocessorSymbols(debug ? DebugSymbols : []), false);
        var renders = output.Split('\n');
        Assert.Equal(2, renders.Length);
        Assert.Equal(renders[0], renders[1]);
        var rendered = renders[0];
        Assert.Matches(@"^<!-- Savanna view: Page\.svn; id=[a-f0-9]{12}; output=utf-8; renderer=compiled; minify=html; blocks=1; partials=1; components=1", rendered);
        Assert.Contains("<!-- Savanna layout: Base.svn", rendered);
        Assert.Equal(20, rendered.Split("<!-- Savanna partial: Card.svn").Length - 1);
        Assert.Equal(20, rendered.Split("<!-- Savanna component: Badge").Length  - 1);
        Assert.DoesNotContain("private model value", rendered);
        Assert.Equal(debug, rendered.Contains("; model=string"));
        Assert.Equal(debug, rendered.Contains("; source=/Templates/Page.svn"));
        Assert.Equal(debug, rendered.Contains("; type=Example.Badge"));
        Assert.All(result.Sources, source => Assert.Contains("<!-- Savanna view:", source.Text));
        Assert.All(result.Sources, source => Assert.Contains("#if DEBUG", source.Text));
    }

    [Fact]
    public async Task CompiledCommentMetadataCannotTerminateTheCommentOrAddFormatting()
    {
        var document = SavannaParser.Parse("/private/path-->\r\n<script>&.svn", "Page.svn", SourceText.From("""<#model type="string">"""));
        var result   = SavannaCompiler.Compile([document], "TestViews");
        var output = await Run(result, """
                                       public static class Entry {
                                           public static async System.Threading.Tasks.Task<string> Run() => await TestViews.Views.Page.RenderToStringAsync("hidden");
                                       }
                                       """, Options.WithPreprocessorSymbols("DEBUG"));
        Assert.Equal("<!-- Savanna view: Page.svn; model=string; source=/private/path&#45;&#45;&gt;  &lt;script&gt;&amp;.svn -->", output);
    }
}

public sealed class SavannaCommentTests
{
    [Fact]
    public void DevelopmentDefaultsToTheHostEnvironmentAndCanBeOverridden()
    {
        using var development = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = Environments.Development,
        }).Build();
        using var production = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = Environments.Production,
        }).Build();
        Assert.True(new SavannaRenderContext(development.Services).IsDevelopment);
        Assert.False(new SavannaRenderContext(production.Services).IsDevelopment);
        Assert.False(new SavannaRenderContext(development.Services, development: false).IsDevelopment);
        Assert.True(new SavannaRenderContext(production.Services, development: true).IsDevelopment);
    }

    [Fact]
    public async Task HandwrittenRenderersOwnTheirOutput()
    {
        var view = new SavannaView<int>("Card", static (writer, _, context) => writer.WriteLiteralAsync("<b/>"u8.ToArray(), context.CancellationToken),
            static (writer,                                     _, context) => writer.WriteLiteralAsync("<small/>"u8.ToArray(), context.CancellationToken));
        using var body    = new MemoryStream();
        using var writer  = new SavannaWriter(body.WriteAsync);
        var       context = new SavannaRenderContext();
        await view.RenderAsync(writer, 0, context);
        await view.RenderPartialAsync(writer, 0, context);
        await context.RenderComponentAsync(writer, new Badge(), "private argument");
        await writer.FlushAsync();
        Assert.Equal("<b/><small/><i/>", Encoding.UTF8.GetString(body.ToArray()));
    }

    private sealed class Badge : ISavannaComponent<string>
    {
        public ValueTask RenderAsync(SavannaWriter writer, string arguments, SavannaRenderContext context) => writer.WriteLiteralAsync("<i/>"u8.ToArray());
    }
}

internal static partial class SavannaTestOutput
{
    public static string NamesOnly(string output) => DetailsPattern().Replace(output, "");

    [GeneratedRegex(@"; id=[a-f0-9]{12}; output=utf-8; renderer=(?:compiled|component)(?:; minify=html; blocks=\d+; partials=\d+; components=\d+)?")]
    private static partial Regex DetailsPattern();
}
