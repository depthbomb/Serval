#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

namespace Serval.Tests.Savanna;

public sealed partial class SavannaCompilerTests
{
    [Fact]
    public async Task StandaloneDirectivesNeedOnlyAnOpeningTag()
    {
        var result = Compile(new Dictionary<string, string>
        {
            ["_Imports.svn"] = """<#using namespace="System.Text">""",
            ["Base.svn"]     = """<#model type="StringBuilder"><#layout><main><#block name="body">base</#block></main>""",
            ["Page.svn"] = """
                            <#model type="StringBuilder"><#extends view="Base.svn"><#import view="Card.svn" as="Card">
                            <#block name="body"><#parent><#let name="label" value="Model.ToString()">
                            <#include view="Card" model="label" /><#html value='SavannaHtml.Trusted("<b>trusted</b>")'><#json id="state" value="label"></#block>
                            """,
            ["Card.svn"] = """<#model type="string"><i>{{ Model }}</i>""",
        });
        var output = await Run(result, """
                                       public static class Entry {
                                           public static async System.Threading.Tasks.Task<string> Run() => await TestViews.Views.Page.RenderToStringAsync(new System.Text.StringBuilder("hello"));
                                       }
                                       """);
        Assert.Contains("<main>base", output);
        Assert.Contains("<i>hello</i>", output);
        Assert.Contains("<b>trusted</b>", output);
        Assert.Contains("<script type=\"application/json\" id=\"state\">\"hello\"</script>", output);
    }

    [Fact]
    public async Task MarkupTagsSupportInheritanceLocalsBranchesAndSinglePassEmptyLoops()
    {
        var result = Compile(new Dictionary<string, string>
        {
            ["Base.svn"] = """<#model type="int[]" /><#layout /><main><#block name="content" required></#block></main>""",
            ["Page.svn"] = """
                            <#model type="int[]" /><#extends view="Base.svn" model="Model" />
                            <#block name="content"><#let name="count" value="Model.Length" />
                            <#if condition="count > 1">many<#elseif condition="count == 1">one<#else>none</#if>
                            <ul><#foreach item="item" in="Model"><li>{{ item }}</li><#empty><li>empty</li></#foreach></ul>
                            </#block>
                            """,
        });
        var output = await Run(result, """
                                       public static class Entry {
                                           public static async System.Threading.Tasks.Task<string> Run() =>
                                               await TestViews.Views.Page.RenderToStringAsync([]) + await TestViews.Views.Page.RenderToStringAsync([1,2]);
                                       }
                                       """);
        Assert.Contains("none<ul><li>empty</li></ul>", output);
        Assert.Contains("many<ul><li>1</li><li>2</li></ul>", output);
    }

    [Fact]
    public async Task FullAttributesUseHtmlSemanticsAndOrdinaryUrls()
    {
        var result = Compile(new Dictionary<string, string>
        {
            ["Page.svn"] = """
                            <#model type="bool" /><input disabled="{{ Model }}" required="{{ (bool?)null }}" aria-disabled="{{ Model }}" title="{{ (string?)null }}"><a href="{{ "custom:open?x=1&y=2" }}">x</a>
                            """,
        });
        var output = await Run(result, """
                                       public static class Entry {
                                           public static async System.Threading.Tasks.Task<string> Run() => await TestViews.Views.Page.RenderToStringAsync(false) + await TestViews.Views.Page.RenderToStringAsync(true);
                                       }
                                       """);
        Assert.Contains("<input aria-disabled=\"false\">", output);
        Assert.Contains("<input disabled aria-disabled=\"true\">", output);
        Assert.Contains("href=\"custom:open?x=1&amp;y=2\"", output);
    }

    [Fact]
    public async Task SlotsCaptureCallerLocalsAndFallbackInTheCallee()
    {
        var result = Compile(new Dictionary<string, string>
        {
            ["Page.svn"] = """
                            <#model type="string" /><#let name="label" value="Model.ToUpperInvariant()" />
                            <#include view="Card.svn" model="Model"><#slot name="heading"><b>{{ label }}</b></#slot></#include>
                            <#include view="Card.svn" model="Model" />
                            """,
            ["Card.svn"] = """<#model type="string" /><article><#slot name="heading"><i>{{ Model }}</i></#slot></article>""",
        });
        var output = await Run(result, """
                                       public static class Entry {
                                           public static async System.Threading.Tasks.Task<string> Run() => await TestViews.Views.Page.RenderToStringAsync("hello");
                                       }
                                       """);
        Assert.Contains("<article><b>HELLO</b></article>", output);
        Assert.Contains("<article><i>hello</i></article>", output);
    }

    [Fact]
    public async Task JsonCannotCloseItsElementAndWhitespaceScopesAreExplicit()
    {
        var result = Compile(new Dictionary<string, string>
        {
            ["Page.svn"] = """
                            <#model type="string" /><#json id="state" value="Model" /><#whitespace mode="preserve">  <b class="x"> a
                              b </b>  </#whitespace><#verbatim>{{ untouched }}</#verbatim>
                            """,
        });
        var output = await Run(result, """
                                       public static class Entry {
                                           public static async System.Threading.Tasks.Task<string> Run() => await TestViews.Views.Page.RenderToStringAsync("</script><script>alert(1)</script>");
                                       }
                                       """);
        Assert.DoesNotContain("<script>alert", output);
        Assert.Contains("\\u003C/script\\u003E", output);
        Assert.Contains("  <b class=\"x\"> a\n  b </b>  ", output);
        Assert.Contains("{{ untouched }}", output);
    }

    [Theory]
    [InlineData("<#if condition=\"true\" />")]
    [InlineData("<#else>")]
    [InlineData("<#if condition=\"true\" other=\"x\"></#if>")]
    [InlineData("<#let name=\"Context\" value=\"1\" />")]
    [InlineData("<#if condition=\"true\" condition=\"false\"></#if>")]
    [InlineData("<#let name=\"value\" value=\"1\"></#let>")]
    [InlineData("<#include view=\"Card.svn\">")]
    [InlineData("<#component type=\"CardComponent\" arguments=\"Model\">")]
    [InlineData("<#slot name=\"body\">")]
    [InlineData("<#debug />")]
    [InlineData("<#release condition=\"true\"></#release>")]
    [InlineData("<#debug></#release>")]
    [InlineData("<#debug>")]
    [InlineData("<#debug><#else></#debug>")]
    [InlineData("<#unless></#unless>")]
    [InlineData("<#unless condition=\"true\" />")]
    [InlineData("<#unless condition=\"true\"></#if>")]
    [InlineData("<#unless condition=\"true\"><#else><#elseif condition=\"false\"></#unless>")]
    [InlineData("<#switch />")]
    [InlineData("<#switch value=\"Model\"><#case></#switch>")]
    [InlineData("<#switch value=\"Model\"><#case value=\"0\"></#case></#switch>")]
    public void MalformedMarkupProducesTemplateDiagnostics(string markup)
    {
        var result = Compile(new Dictionary<string, string> { ["Page.svn"] = "<#model type=\"int\" />" + markup });
        Assert.NotEmpty(result.Diagnostics);
    }

    [Fact]
    public async Task LoopsEnumerateOnceAndSupportAsyncAndForEmptyBranches()
    {
        var result = Compile(new Dictionary<string, string>
        {
            ["Page.svn"] = """
                            <#model type="Sequence" /><#foreach item="item" in="Model">{{ item }}<#empty>empty</#foreach>
                            <#foreach item="item" in="Model.Async()" await>{{ item }}<#empty>async-empty</#foreach>
                            <#for clause="var i = 0; i < 0; i++">never<#empty>for-empty</#for>
                            """,
        });
        var output = await Run(result, """
                                       public sealed class Sequence : System.Collections.Generic.IEnumerable<int> {
                                           private bool used;
                                           public System.Collections.Generic.IEnumerator<int> GetEnumerator() {
                                               if (used) throw new System.Exception("Enumerated twice");
                                               used = true; yield return 7;
                                           }
                                           System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
                                           public async System.Collections.Generic.IAsyncEnumerable<int> Async() { await System.Threading.Tasks.Task.Yield(); yield break; }
                                       }
                                       public static class Entry {
                                           public static async System.Threading.Tasks.Task<string> Run() => await TestViews.Views.Page.RenderToStringAsync(new Sequence());
                                       }
                                       """);
        Assert.Contains("7", output);
        Assert.Contains("async-empty", output);
        Assert.Contains("for-empty", output);
        Assert.DoesNotContain("never", output);
    }

    [Fact]
    public void SlotsValidateSuppliesAndRcDataIncludesAreRejected()
    {
        foreach (var caller in new[]
                 {
                     "<#include view=\"Card.svn\" />",
                     "<#include view=\"Card.svn\"><#slot name=\"typo\">x</#slot></#include>",
                     "<#include view=\"Card.svn\">unassigned</#include>",
                 })
        {
            var result = Compile(new Dictionary<string, string>
            {
                ["Page.svn"] = "<#model type=\"int\" />" + caller,
                ["Card.svn"] = "<#model type=\"int\" /><#slot name=\"body\" required />",
            });
            Assert.NotEmpty(result.Diagnostics);
        }

        var rcData = Compile(new Dictionary<string, string>
        {
            ["Page.svn"] = "<#model type=\"int\" /><#extends view=\"Base.svn\" /><#block name=\"title\"><#include view=\"Card.svn\" /></#block>",
            ["Base.svn"] = "<#model type=\"int\" /><#layout /><title><#block name=\"title\"></#block></title>",
            ["Card.svn"] = "<#model type=\"int\" />x",
        });
        Assert.Contains(rcData.Diagnostics, diagnostic => diagnostic.Code == "SAVANNA003");
    }

    [Fact]
    public async Task SharedImportsAreScopedAndSupportNestedSlots()
    {
        var result = Compile(new Dictionary<string, string>
        {
            ["_Imports.svn"] = "<#using namespace=\"System.Text\" />",
            ["Page.svn"]     = """<#model type="StringBuilder" /><#include view="Card.svn"><#slot name="body"><#include view="Card.svn"><#slot name="body">{{ Model }}</#slot></#include></#slot></#include>""",
            ["Card.svn"]     = """<#model type="StringBuilder" /><b><#slot name="body" required /></b>""",
        });
        Assert.Equal(2, result.Sources.Count);
        Assert.Contains("<b><!-- Savanna partial: Card.svn --><b>ok</b></b>", await Run(result, """
                                                                                              public static class Entry {
                                                                                                  public static async System.Threading.Tasks.Task<string> Run() => await TestViews.Views.Page.RenderToStringAsync(new System.Text.StringBuilder("ok"));
                                                                                              }
                                                                                              """));
    }

    [Fact]
    public async Task PairedIncludesAndSlotOutletsPropagatePreservedWhitespace()
    {
        var result = Compile(new Dictionary<string, string>
        {
            ["Page.svn"] = """<#model type="int" /><pre><#include view="Card.svn"><#slot name="body">  a   b  </#slot></#include></pre><#include view="Pre.svn"><#slot name="body">  c   d  </#slot></#include>""",
            ["Card.svn"] = """<#model type="int" /><#slot name="body" required />""",
            ["Pre.svn"]  = """<#model type="int" /><pre><#slot name="body" required /></pre>""",
        });
        var output = await Run(result, """
                                       public static class Entry {
                                           public static async System.Threading.Tasks.Task<string> Run() => await TestViews.Views.Page.RenderToStringAsync(0);
                                       }
                                       """);
        Assert.Contains("  a   b  ", output);
        Assert.Contains("<pre>  c   d  </pre>", output);
    }

    [Fact]
    public async Task AttributeSpellingAndEnumeratedHiddenValuesSurviveCompilation()
    {
        var result = Compile(new Dictionary<string, string>
        {
            ["Page.svn"] = """
                            <#model type="int" /><#-- {{ do not compile }} <#if> -->
                            <svg viewBox="{{ "0 0 20 20" }}"></svg><div hidden="{{ false }}"></div><div hidden="{{ "until-found" }}"></div>
                            """,
        });
        var output = await Run(result, """
                                       public static class Entry {
                                           public static async System.Threading.Tasks.Task<string> Run() => await TestViews.Views.Page.RenderToStringAsync(0);
                                       }
                                       """);
        Assert.Contains("viewBox=\"0 0 20 20\"", output);
        Assert.Contains("<div></div><div hidden=\"until-found\"></div>", output);
        Assert.DoesNotContain("do not compile", output);
    }

    [Fact]
    public async Task ComponentsForwardNamedContentToTheirViews()
    {
        var result = Compile(new Dictionary<string, string>
        {
            ["Page.svn"] = """<#model type="string" /><#component type="CardComponent" arguments="Model"><#slot name="body">{{ Model }}</#slot></#component>""",
            ["Card.svn"] = """<#model type="string" /><section><#slot name="body" required /></section>""",
        });
        var output = await Run(result, """
                                       public sealed class CardComponent : Serval.Savanna.ISavannaComponent<string> {
                                           public System.Threading.Tasks.ValueTask RenderAsync(Serval.Savanna.SavannaWriter writer, string arguments, Serval.Savanna.SavannaRenderContext context)
                                               => TestViews.Views.Card.RenderAsync(writer, arguments, context);
                                       }
                                       public sealed class Services : System.IServiceProvider {
                                           public object? GetService(System.Type type) => type == typeof(CardComponent) ? new CardComponent() : null;
                                       }
                                       public static class Entry {
                                           public static async System.Threading.Tasks.Task<string> Run() => await TestViews.Views.Page.RenderToStringAsync("component body", new Serval.Savanna.SavannaRenderContext(new Services()));
                                       }
                                       """);
        Assert.Contains("<section>component body</section>", output);
    }

    [Fact]
    public async Task OverriddenBlocksDoNotRequireSlotsFromUnusedParentBodies()
    {
        var result = Compile(new Dictionary<string, string>
        {
            ["Base.svn"]  = """<#model type="int" /><#layout /><#block name="body"><#slot name="unused" required /></#block>""",
            ["Child.svn"] = """<#model type="int" /><#extends view="Base.svn" /><#block name="body">child</#block>""",
            ["Page.svn"]  = """<#model type="int" /><#include view="Child.svn" />""",
        });
        var output = await Run(result, """
                                       public static class Entry {
                                           public static async System.Threading.Tasks.Task<string> Run() => await TestViews.Views.Page.RenderToStringAsync(0);
                                       }
                                       """);
        Assert.EndsWith("child", output);
    }
}
