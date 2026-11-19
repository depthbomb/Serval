#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.DependencyInjection;
using Serval.Generated;
using Serval.Routing;
using Serval.Tests.Support;

namespace Serval.Tests.Core.Routing;

public sealed class GeneratedRouteTests
{
    [Theory]
    [InlineData("/a/{id}/{ID}")]
    [InlineData("/a/{id?}/required")]
    [InlineData("/a/{*tail}/required")]
    [InlineData("/a/{***tail}")]
    [InlineData("/a/{id:int=oops}")]
    [InlineData("/a/{id:range(1,5)=9}")]
    [InlineData("/a/{id:int(1)}")]
    [InlineData("/a/{id:range(5,1)}")]
    [InlineData("/a/{id:minlength(-1)}")]
    [InlineData("/a/{id:length(1.5)}")]
    [InlineData("/a/{id:regex([)}")]
    [InlineData("/a/{id:range(1,2}")]
    [InlineData("/a//b")]
    [InlineData("/a/prefix-{id}")]
    public void InvalidRouteDeclarationsFailCompilation(string route)
    {
        var (_, diagnostics) = Generate($"[Get({SymbolDisplay.FormatLiteral(route, true)})] public ValueTask<IActionResult> Action() => default;");
        Assert.Contains(diagnostics, diagnostic => diagnostic is { Id: "SERVAL003", Location.IsInSource: true });
    }

    [Theory]
    [InlineData("[Get(\"/a/{id:int}\")] public ValueTask<IActionResult> First() => default; [Get(\"/a/{other:int}\")] public ValueTask<IActionResult> Second() => default;", "SERVAL005")]
    [InlineData("[Get(\"/a\", Name = \"same\")] public ValueTask<IActionResult> First() => default; [Get(\"/b\", Name = \"same\")] public ValueTask<IActionResult> Second() => default;", "SERVAL005")]
    [InlineData("[Get(\"/a\", Name = \"a.b\")] public ValueTask<IActionResult> First() => default; [Get(\"/b\", Name = \"a-b\")] public ValueTask<IActionResult> Second() => default;", "SERVAL006")]
    [InlineData("[Get(\"/{1}/{_1}\")] public ValueTask<IActionResult> First() => default;", "SERVAL006")]
    [InlineData("[Get(\"/a\")] public ValueTask<IActionResult> First([FromRoute] int id) => default;", "SERVAL004")]
    [InlineData("[Get(\"/{id?}\")] public ValueTask<IActionResult> First([FromRoute] int id) => default;", "SERVAL004")]
    [InlineData("[Get(\"/a\")] public ValueTask<IActionResult> First([FromQuery, FromHeader] int id) => default;", "SERVAL004")]
    [InlineData("[Post(\"/a\")] public ValueTask<IActionResult> First([FromBody] string a, [FromBody] string b) => default;", "SERVAL004")]
    [InlineData("[Post(\"/a\")] public ValueTask<IActionResult> First([FromBody] string? a = null) => default;", "SERVAL004")]
    [InlineData("[Post(\"/a\")] public ValueTask<IActionResult> First([FromBody] string a, [FromForm] string b) => default;", "SERVAL004")]
    [InlineData("[Post(\"/a\")] public ValueTask<IActionResult> First([FromForm] string a, [FromBody] string b) => default;", "SERVAL004")]
    [InlineData("[Get(\"/a\")] public ValueTask<IActionResult> First([FromQuery] object a) => default;", "SERVAL004")]
    [InlineData("[Get(\"/a\")] public ValueTask<IActionResult> First([FromQuery] int[,] a) => default;", "SERVAL004")]
    [InlineData("[Get(\"/a\")] public ValueTask<IActionResult> First([FromQuery(\"\")] int a) => default;", "SERVAL004")]
    [InlineData("[Get(\"/a\")] public ValueTask<IActionResult> First([FromQuery] RequestContext context) => default;", "SERVAL004")]
    [InlineData("[Get(\"/a/{id}\"), Get(\"/b/{other}\")] public ValueTask<IActionResult> First([FromRoute] int id) => default;", "SERVAL004")]
    public void AmbiguousRoutesAndUnsupportedBindingsHaveActionableDiagnostics(string members, string id)
    {
        var (_, diagnostics) = Generate(members);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == id);
    }

    [Fact]
    public void ConfigurationDependentRoutesAndCustomConstraintsRemainValid()
    {
        var (compilation, diagnostics) = Generate("""
                                                  [Get("/a/{id:slug=hello}")] public ValueTask<IActionResult> First() => default;
                                                  [Get("/A/{id:slug=hello}")] public ValueTask<IActionResult> Second() => default;
                                                  [Get("/a/{id:slug=hello}/")] public ValueTask<IActionResult> Third() => default;
                                                  [Get("/regex/{value:regex(^[a-z]+$)=HELLO}")] public ValueTask<IActionResult> Fourth() => default;
                                                  """);
        Assert.Empty(diagnostics);
        Assert.DoesNotContain(compilation.GetDiagnostics(), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Theory]
    [InlineData("[FromQuery] decimal value = 1.25m")]
    [InlineData("[FromQuery] double value = double.NaN")]
    [InlineData("[FromQuery] float value = float.PositiveInfinity")]
    [InlineData("[FromQuery] ulong value = ulong.MaxValue")]
    [InlineData("[FromQuery] long value = long.MinValue")]
    [InlineData("[FromQuery] char value = '\\''")]
    [InlineData("[FromQuery] DateTime value = default")]
    [InlineData("[FromQuery] string value = null!")]
    [InlineData("[FromQuery] int[] values = null!")]
    [InlineData("[FromQuery] string[]? values = null")]
    [InlineData("[FromQuery] DayOfWeek? value = DayOfWeek.Friday")]
    [InlineData("[FromServices] TimeProvider? clock = null")]
    public void GeneratedBindingsPreserveCSharpDefaults(string parameter)
    {
        var (compilation, diagnostics) = Generate($"[Get(\"/\")] public ValueTask<IActionResult> Action({parameter}) => default;");
        Assert.Empty(diagnostics);
        Assert.DoesNotContain(compilation.GetDiagnostics(), diagnostic => diagnostic.Severity >= DiagnosticSeverity.Warning);
    }

    [Fact]
    public void GeneratedHelpersHandleKeywordsReservedNamesAndOptionalTypedArguments()
    {
        var (compilation, diagnostics) = Generate("""
                                                  [Get("/{class:int}/{query}/{__urls}/{1}/{page:long=2}/{**tail}", Name = "links.show")]
                                                  public ValueTask<IActionResult> First() => default;
                                                  [Get("/", Name = "GetType")]
                                                  public ValueTask<IActionResult> Second() => default;
                                                  public string Link(IUrlGenerator urls) => urls.LinksShow(@class: 4, query: "search", __urls: "name", _1: "first", page: 3, tail: "a/b");
                                                  public string Root(IUrlGenerator urls) => urls.GetTypePath();
                                                  """);
        Assert.Empty(diagnostics);
        Assert.DoesNotContain(compilation.GetDiagnostics(), diagnostic => diagnostic.Severity >= DiagnosticSeverity.Warning);
    }

    [Theory]
    [InlineData("urls.Item()")]
    [InlineData("urls.Item(\"oops\")")]
    public void TypedHelpersRejectMissingAndIncompatibleRequiredValues(string call)
    {
        var (compilation, diagnostics) = Generate($$"""
                                                    [Get("/items/{id:int}", Name = "item")] public ValueTask<IActionResult> First() => default;
                                                    public string Link(IUrlGenerator urls) => {{call}};
                                                    """);
        Assert.Empty(diagnostics);
        Assert.Contains(compilation.GetDiagnostics(), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public async Task TypedHelpersPreserveRouteDefaultsConstraintsEscapingAndSlashOptions()
    {
        await using var app  = new TestApplication(options => options.TrailingSlashes = TrailingSlashBehavior.RedirectToWithSlash);
        var             urls = app.Services.GetRequiredService<IUrlGenerator>();
        Assert.Equal("/test/items/12/?q=a%20b", urls.Item(12, new Dictionary<string, object?>
        {
            ["q"]    = "a b",
            ["skip"] = null,
        }));
        Assert.Equal("/test/optional/1/", urls.Optional());
        Assert.Equal("/test/optional/3/", urls.Optional(3));
        Assert.Equal("/test/files/a/b%20c/", urls.Files("a/b c"));
        Assert.Equal("/test/files/", urls.Files());
        Assert.Throws<ArgumentException>(() => urls.Item(0));
    }

    internal static (Compilation Compilation, IEnumerable<Diagnostic> Diagnostics) Generate(string members)
        => GeneratorTests.Generate($$"""
                                     using System;
                                     using System.Threading;
                                     using System.Threading.Tasks;
                                     using Serval;
                                     using Serval.Binding;
                                     using Serval.Generated;
                                     using Serval.Http;
                                     using Serval.Routing;
                                     [Controller]
                                     public class Demo
                                     {
                                         {{members}}
                                     }
                                     """);
}
