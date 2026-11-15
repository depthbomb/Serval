#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.CodeAnalysis.Text;
using System.Text.Json;
using Serval.Savanna.Compiler.Parsing;
using Serval.Savanna.LanguageServer.Features;
using Serval.Savanna.LanguageServer.Rpc;

namespace Serval.Savanna.LanguageServer.Tests;

public sealed class TemplateFormatterTests
{
    [Fact]
    public void FormattingChangesOnlyHeaderSpacing()
    {
        const string source   = "<#model   type = 'int'/>\n<pre>  keep\n this </pre><#if   condition = \"Model >\n 0\" > x </#if   >";
        var          document = SavannaParser.Parse("Page.svn", "Page.svn", SourceText.From(source));
        var          edits    = JsonSerializer.SerializeToElement(TemplateFormatter.Format(document));
        var changes = edits.EnumerateArray().Select(edit => new TextChange(TextSpan.FromBounds(
            Position.Read(edit.GetProperty("range").GetProperty("start")).Offset(document.Text),
            Position.Read(edit.GetProperty("range").GetProperty("end")).Offset(document.Text)), edit.GetProperty("newText").GetString()!));
        var result = document.Text.WithChanges(changes).ToString();
        Assert.Equal("<#model type='int'>\n<pre>  keep\n this </pre><#if condition=\"Model >\n 0\"> x </#if>", result);
        Assert.Empty(TemplateFormatter.Format(SavannaParser.Parse("Page.svn", "Page.svn", SourceText.From(result))));
    }

    [Fact]
    public void UnterminatedAttributesAreNotReformatted()
    {
        var document = SavannaParser.Parse("Page.svn", "Page.svn", SourceText.From("<#model type=\"unfinished"));
        Assert.Empty(TemplateFormatter.Format(document));
    }

    [Theory]
    [InlineData("<#model type=\"int\" />", "<#model type=\"int\">")]
    [InlineData("<#using namespace=\"System\" />", "<#using namespace=\"System\">")]
    [InlineData("<#layout />", "<#layout>")]
    [InlineData("<#extends view=\"Base.svn\" />", "<#extends view=\"Base.svn\">")]
    [InlineData("<#import view=\"Card.svn\" as=\"Card\" />", "<#import view=\"Card.svn\" as=\"Card\">")]
    [InlineData("<#parent />", "<#parent>")]
    [InlineData("<#let name=\"x\" value=\"1\" />", "<#let name=\"x\" value=\"1\">")]
    [InlineData("<#html value=\"Model\" />", "<#html value=\"Model\">")]
    [InlineData("<#json id=\"state\" value=\"Model\" />", "<#json id=\"state\" value=\"Model\">")]
    public void FormattingPrefersBareTerminatorsForStandaloneTags(string source, string expected)
    {
        var document = SavannaParser.Parse("Page.svn", "Page.svn", SourceText.From(source));
        var edits    = JsonSerializer.SerializeToElement(TemplateFormatter.Format(document));
        Assert.Equal(expected, Assert.Single(edits.EnumerateArray()).GetProperty("newText").GetString());
        Assert.Empty(TemplateFormatter.Format(SavannaParser.Parse("Page.svn", "Page.svn", SourceText.From(expected))));
    }

    [Theory]
    [InlineData("<#include view=\"Card.svn\" />")]
    [InlineData("<#component type=\"CardComponent\" arguments=\"Model\" />")]
    [InlineData("<#slot name=\"body\" required />")]
    public void FormattingPreservesTheEmptyFormOfTagsThatCanHaveBodies(string source)
    {
        var document = SavannaParser.Parse("Page.svn", "Page.svn", SourceText.From(source));
        Assert.Empty(TemplateFormatter.Format(document));
    }
}
