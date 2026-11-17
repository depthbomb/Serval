#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.CodeAnalysis;

namespace Serval.Tests.Savanna;

public sealed partial class SavannaCompilerTests
{
    private const string LoopModel = """
                                     public sealed class Sequence {
                                         public int[] Items { get; set; } = [];
                                         public int Skips { get; private set; }
                                         public int Stops { get; private set; }
                                         public bool Skip(int item) { Skips++; return item % 2 == 0; }
                                         public bool Stop(int item) { Stops++; return item >= 3; }
                                         public async System.Collections.Generic.IAsyncEnumerable<int> Async() {
                                             await System.Threading.Tasks.Task.CompletedTask;
                                             foreach (var item in Items) { yield return item; }
                                         }
                                     }
                                     """;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BuildTagsCompileOnlyTheSelectedDebugBranch(bool debug)
    {
        var result = Compile(new Dictionary<string, string>
        {
            ["Page.svn"] = """
                           <#model type="int"><p>shared</p>
                           <#debug><#let name="label" value="DebugOnly.Label"><span>{{ label }}</span>
                               <#debug><b>nested debug</b></#debug><#release>{{ UndefinedInEveryBuild }}</#release>
                           </#debug>
                           <#release><#let name="label" value="ReleaseOnly.Label"><span>{{ label }}</span>
                               <#release><b>nested release</b></#release><#debug>{{ UndefinedInEveryBuild }}</#debug>
                           </#release>
                           """,
        });
        var output = await Run(result, """
                                       #if DEBUG
                                       public static class DebugOnly { public static string Label => "debug"; }
                                       #else
                                       public static class ReleaseOnly { public static string Label => "release"; }
                                       #endif
                                       public static class Entry {
                                           public static async System.Threading.Tasks.Task<string> Run() => await TestViews.Views.Page.RenderToStringAsync(0);
                                       }
                                       """, Options.WithPreprocessorSymbols(debug ? new[] { "DEBUG" } : []));
        Assert.Contains("<p>shared</p>", output);
        Assert.Contains(debug ? "<span>debug</span>" : "<span>release</span>", output);
        Assert.Contains(debug ? "<b>nested debug</b>" : "<b>nested release</b>", output);
        Assert.DoesNotContain(debug ? "release" : "debug", output);
    }

    [Theory]
    [InlineData(0, "empty")]
    [InlineData(1, "one")]
    [InlineData(2, "many")]
    public async Task UnlessNegatesTheWholeConditionOnceAndSupportsBranches(int count, string expected)
    {
        var result = Compile(new Dictionary<string, string>
        {
            ["Page.svn"] = """
                           <#model type="Probe"><#unless condition="Model.HasItems() && true // keep the condition mapped">empty<#elseif condition="Model.Count == 1">one<#else>many</#unless>
                           """,
        });
        var output = await Run(result, $$"""
                                         public sealed class Probe {
                                             public int Count { get; set; }
                                             public int Calls { get; private set; }
                                             public bool HasItems() { Calls++; return Count > 0; }
                                         }
                                         public static class Entry {
                                             public static async System.Threading.Tasks.Task<string> Run() {
                                                 var model = new Probe { Count = {{count}} };
                                                 return await TestViews.Views.Page.RenderToStringAsync(model) + "|" + model.Calls;
                                             }
                                         }
                                         """);
        Assert.EndsWith(expected + "|1", output);
    }

    [Theory]
    [InlineData(0, "small")]
    [InlineData(1, "small")]
    [InlineData(2, "two")]
    [InlineData(4, "range")]
    [InlineData(9, "other")]
    public async Task SwitchSupportsGroupedLabelsPatternsAndDefault(int value, string expected)
    {
        var result = Compile(new Dictionary<string, string>
        {
            ["Page.svn"] = """
                           <#model type="int"><span>before <#switch value="Model">
                               <#case value="0">
                               <#-- These labels share a body. -->
                               {# Either comment syntax can go here. #}
                               <#case value="1">small<#break>
                               <#case value="2">two<#break>
                               <#case value=">= 3 and <= 5">range<#break>
                               <#default>other<#break>
                           </#switch> after</span>
                           """,
        });
        var output = await Run(result, $$"""
                                         public static class Entry {
                                             public static async System.Threading.Tasks.Task<string> Run() => await TestViews.Views.Page.RenderToStringAsync({{value}});
                                         }
                                         """);
        Assert.Contains("<span>before " + expected + " after</span>", output);
    }

    [Fact]
    public async Task NestedSwitchesAndLoopsUseTheNearestJumpTarget()
    {
        var result = Compile(new Dictionary<string, string>
        {
            ["Page.svn"] = """
                           <#model type="int[]"><#foreach item="item" in="Model"><#switch value="item">
                               <#case value="0"><#continue>
                               <#case value="1"><#break if="item == 1">unreachable at runtime<#break>
                               <#default><#switch value="item % 2">
                                   <#case value="0">even<#break>
                                   <#default>odd<#break>
                               </#switch><#break>
                           </#switch>!<span>{{ item }}</span></#foreach>
                           """,
        });
        var output = await Run(result, """
                                       public static class Entry {
                                           public static async System.Threading.Tasks.Task<string> Run() => await TestViews.Views.Page.RenderToStringAsync([0, 1, 2, 3]);
                                       }
                                       """);
        Assert.EndsWith("!<span>1</span>even!<span>2</span>odd!<span>3</span>", output);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BuildConditionalJumpsAllowFormattingWithWarningsAsErrors(bool debug)
    {
        var result = Compile(new Dictionary<string, string>
        {
            ["Page.svn"] = """
                           <#model type="int"><#foreach item="item" in="new[] { Model }">
                               <#debug><#continue></#debug>
                               <#release><b>loop</b></#release>
                           </#foreach><#switch value="Model">
                               <#case value="1">
                                   <#debug><b>debug case</b><#break></#debug>
                                   <#release><b>release case</b><#break></#release>
                           </#switch>
                           """,
        });
        var options = Options.WithPreprocessorSymbols(debug ? new[] { "DEBUG" } : []);
        const string code = """
                            public static class Entry {
                                public static async System.Threading.Tasks.Task<string> Run() => await TestViews.Views.Page.RenderToStringAsync(1);
                            }
                            """;
        Assert.DoesNotContain(CreateCompilation(result, code, options).GetDiagnostics(), diagnostic => diagnostic.Severity >= DiagnosticSeverity.Warning);
        var output = await Run(result, code, options);
        Assert.Contains(debug ? "<b>debug case</b>" : "<b>release case</b>", output);
        Assert.Equal(!debug, output.Contains("<b>loop</b>"));
    }

    [Theory]
    [InlineData("<#debug><textarea></#debug></textarea>")]
    [InlineData("<#release><title></#release></title>")]
    [InlineData("<#unless condition='Model > 0'><textarea><#else></textarea></#unless>")]
    [InlineData("<#switch value='Model'><#case value='0'><textarea><#break><#default></textarea><#break></#switch>")]
    [InlineData("<#foreach item='item' in='new[] { Model }'><textarea><#break if='item == 1'></textarea></#foreach>")]
    [InlineData("<#for clause='int i = 0; i < Model; i++'><title><#continue></title></#for>")]
    [InlineData("<textarea><#foreach item='item' in='new[] { Model }'></textarea><#break><textarea></#foreach></textarea>")]
    public void BranchesAndJumpsMustPreserveHtmlTextContainers(string markup)
    {
        var result = Compile(new Dictionary<string, string>
        {
            ["Page.svn"] = "<#model type='int'>" + markup
        });
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "SAVANNA003");
        Assert.Empty(result.Sources);
    }

    [Fact]
    public void LayoutBlockBranchesUseTheLayoutsHtmlTextContext()
    {
        var result = Compile(new Dictionary<string, string>
        {
            ["Base.svn"] = """<#model type="int"><#layout><textarea><#block name="Body" required></#block></textarea>""",
            ["Page.svn"] = """<#model type="int"><#extends view="Base.svn"><#block name="Body"><#debug></textarea></#debug></#block>""",
        });
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "SAVANNA003" && diagnostic.Path.EndsWith("Page.svn"));
        Assert.Empty(result.Sources);
    }

    [Fact]
    public async Task ControlFlowInsideTextContainersKeepsEncodedTextAndLoopTargets()
    {
        var result = Compile(new Dictionary<string, string>
        {
            ["Page.svn"] = """
                           <#model type="string"><textarea><#foreach item="item" in="new[] { 0, 1, 2 }"><#continue if="item == 0"><#switch value="item"><#case value="1"><#unless condition="Model.Length == 0">{{ Model }}</#unless><#break><#default><#break></#switch><#break if="item == 1"></#foreach></textarea>
                           """,
        });
        var output = await Run(result, """
                                       public static class Entry {
                                           public static async System.Threading.Tasks.Task<string> Run() => await TestViews.Views.Page.RenderToStringAsync("</textarea><script>");
                                       }
                                       """);
        Assert.EndsWith("<textarea>&lt;/textarea&gt;&lt;script&gt;</textarea>", output);
    }

    [Theory]
    [InlineData("foreach")]
    [InlineData("awaitforeach")]
    [InlineData("for")]
    public async Task LoopJumpsEvaluateConditionsOnceAndKeepEmptyIterationSemantics(string loop)
    {
        var (open, close) = LoopTags(loop);
        var result = Compile(new Dictionary<string, string>
        {
            ["Page.svn"] = "<#model type=\"Sequence\">"                                                                        + open +
                           "<#continue if=\"Model.Skip(item)\"><#break if=\"Model.Stop(item)\"><i>{{ item }}</i><#empty>empty" + close,
        });
        var output = await Run(result, LoopModel + """
                                                   public static class Entry {
                                                       public static async System.Threading.Tasks.Task<string> Run() {
                                                           var model = new Sequence { Items = [0, 1, 2, 3, 4] };
                                                           return await TestViews.Views.Page.RenderToStringAsync(model) + "|" + model.Skips + "," + model.Stops
                                                               + "|" + await TestViews.Views.Page.RenderToStringAsync(new Sequence { Items = [] })
                                                               + "|" + await TestViews.Views.Page.RenderToStringAsync(new Sequence { Items = [0, 2] });
                                                       }
                                                   }
                                                   """);
        Assert.Contains("<i>1</i>|4,2|", output);
        Assert.Contains("empty|", output);
        Assert.Equal(1, output.Split("empty").Length - 1);
        Assert.DoesNotContain("<i>3</i>", output);
    }

    [Fact]
    public async Task NestedLoopJumpsStayInTheirOwnLoopAndEmptyCanJumpToTheOuterLoop()
    {
        var result = Compile(new Dictionary<string, string>
        {
            ["Page.svn"] = """
                           <#model type="int[]"><#foreach item="outer" in="Model"><#for clause="int inner = 0; inner < 4; inner++"><#continue if="inner == 0"><#break if="inner == 2"><i>{{ outer }}:{{ inner }}</i></#for><#foreach item="unused" in="System.Array.Empty<int>()"><#empty><#continue if="outer == 1"><#break if="outer == 2"></#foreach>after</#foreach>
                           """,
        });
        var output = await Run(result, """
                                       public static class Entry {
                                           public static async System.Threading.Tasks.Task<string> Run() => await TestViews.Views.Page.RenderToStringAsync([0, 1, 2, 3]);
                                       }
                                       """);
        Assert.EndsWith("<i>0:1</i>after<i>1:1</i><i>2:1</i>", output);
    }

    [Fact]
    public async Task SwitchPreservesWhitespaceBetweenALabelAndItsRenderedContent()
    {
        var result = Compile(new Dictionary<string, string>
        {
            ["Page.svn"] = """<#model type="int"><span>before<#switch value="Model"><#case value="1"> {{ Model }}<#break></#switch></span>""",
        });
        var output = await Run(result, """
                                       public static class Entry {
                                           public static async System.Threading.Tasks.Task<string> Run() => await TestViews.Views.Page.RenderToStringAsync(1);
                                       }
                                       """);
        Assert.EndsWith("<span>before 1</span>", output);
    }

    [Theory]
    [InlineData("foreach", "break", "<i>0</i>")]
    [InlineData("awaitforeach", "break", "<i>0</i>")]
    [InlineData("for", "break", "<i>0</i>")]
    [InlineData("foreach", "continue", "<i>0</i><i>1</i><i>2</i>")]
    [InlineData("awaitforeach", "continue", "<i>0</i><i>1</i><i>2</i>")]
    [InlineData("for", "continue", "<i>0</i><i>1</i><i>2</i>")]
    public async Task LoopJumpsCanBeUnconditional(string loop, string jump, string expected)
    {
        var (open, close) = LoopTags(loop);
        var result = Compile(new Dictionary<string, string>
        {
            ["Page.svn"] = "<#model type=\"Sequence\">" + open + "<i>{{ item }}</i><#" + jump + ">\n    " + close,
        });
        var output = await Run(result, LoopModel + """
                                                   public static class Entry {
                                                       public static async System.Threading.Tasks.Task<string> Run()
                                                           => await TestViews.Views.Page.RenderToStringAsync(new Sequence { Items = [0, 1, 2] });
                                                   }
                                                   """);
        Assert.EndsWith(expected, output);
    }

    [Theory]
    [InlineData("<#case value='1'>")]
    [InlineData("<#default>")]
    [InlineData("<#break>")]
    [InlineData("<#continue>")]
    [InlineData("<#switch value='Model'>text</#switch>")]
    [InlineData("<#switch value='Model'>{{ Model }}</#switch>")]
    [InlineData("<#switch value='Model'><#let name='x' value='1'></#switch>")]
    [InlineData("<#switch value='Model'><#case value='0'><#if condition='true'><#case value='1'></#if></#switch>")]
    [InlineData("<#switch value='Model'><#default><#break><#default><#break></#switch>")]
    [InlineData("<#switch value='Model'><#case value='0'><#continue></#switch>")]
    [InlineData("<#foreach item='x' in='new int[0]'><#break if=''></#foreach>")]
    [InlineData("<#foreach item='x' in='new int[0]'><#continue condition='true'></#foreach>")]
    [InlineData("<#foreach item='x' in='new int[0]'><#empty><#break></#foreach>")]
    [InlineData("<#for clause='int i = 0; i < 0; i++'><#empty><#continue></#for>")]
    [InlineData("<#foreach item='x' in='new int[0]'><#include view='Card.svn'><#slot name='Body'><#break></#slot></#include></#foreach>")]
    public void SwitchLabelsAndJumpsRequireValidContexts(string markup)
    {
        var result = Compile(new Dictionary<string, string>
        {
            ["Page.svn"] = "<#model type='int'>" + markup
        });
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "SAVANNA001");
    }

    [Theory]
    [InlineData("<#case value='0'>one<#case value='1'>two<#break>")]
    [InlineData("<#case value='0'>one<#break><#case value='0'>two<#break>")]
    public void SwitchCasesAreCheckedByTheCSharpCompiler(string cases)
    {
        var result = Compile(new Dictionary<string, string>
        {
            ["Page.svn"] = "<#model type='int'><#switch value='Model'>" + cases + "</#switch>"
        });
        Assert.Contains(CreateCompilation(result, "").GetDiagnostics(), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    private static (string Open, string Close) LoopTags(string loop) => loop switch
    {
        "for"          => ("<#for clause='var index = 0; index < Model.Items.Length; index++'><#let name='item' value='Model.Items[index]'>", "</#for>"),
        "awaitforeach" => ("<#foreach item='item' in='Model.Async()' await>", "</#foreach>"),
        _              => ("<#foreach item='item' in='Model.Items'>", "</#foreach>"),
    };
}
