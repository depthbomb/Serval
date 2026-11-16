#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.Extensions.DependencyInjection;
using Serval.Hosting;
using Serval.Http;
using Serval.Http.Constants;
using Serval.Http.Results;
using Serval.Routing;
using Serval.Tests.Support;

namespace Serval.Tests.Core.Routing;

public sealed class RouteIndexTests
{
    [Theory]
    [InlineData("/api/products/7/details", "GET", "product:7")]
    [InlineData("/api/products/current", "GET", "current")]
    [InlineData("/api/products/current", "POST", "posted")]
    [InlineData("/api/products/current", "HEAD", "head:products:current")]
    [InlineData("/api/books/a%2Fb", "GET", "category:books:a/b")]
    [InlineData("/api/products/invalid/details", "GET", "api:products/invalid/details")]
    [InlineData("/api/products", "GET", "api:products")]
    [InlineData("/api", "GET", "api:")]
    [InlineData("/other/path", "GET", "root:other/path")]
    [InlineData("/", "GET", "root:")]
    public async Task LiteralPrefixesKeepAncestorParametersCatchAllsAndExplicitHeadRoutes(string path, string method, string expected)
    {
        await using var app     = Application();
        await using var request = app.Request("http://localhost" + path, method: method);
        await app.Services.GetRequiredService<IRequestHandler>().HandleAsync(request.Context.Request, request.Response, CancellationToken.None);
        Assert.Equal(StatusCodes.Ok, request.Response.StatusCode);
        Assert.Equal(expected, request.Response.Text);
    }

    [Theory]
    [InlineData("OPTIONS", 204)]
    [InlineData("DELETE", 405)]
    public async Task AllowedMethodsIncludeMatchesFromDifferentPrefixLevels(string method, int expected)
    {
        await using var app     = Application();
        await using var request = app.Request("http://localhost/api/products/current", method: method);
        await app.Services.GetRequiredService<IRequestHandler>().HandleAsync(request.Context.Request, request.Response, CancellationToken.None);
        Assert.Equal(expected, request.Response.StatusCode);
        Assert.Equal("GET, HEAD, OPTIONS, POST", request.Response.Headers[HeaderNames.Allow]);
    }

    [Theory]
    [InlineData(false, "/API/A%2Fb/DETAILS", 200)]
    [InlineData(true, "/API/A%2Fb/DETAILS", 404)]
    [InlineData(true, "/api/a%2Fb/details", 200)]
    [InlineData(false, "/api/a/b/details", 404)]
    public async Task PrefixLookupHonorsCaseSensitivityAndEncodedSegmentBoundaries(bool caseSensitive, string path, int expected)
    {
        await using var app = new SecurityTestApplication(builder =>
        {
            builder.Options.CaseSensitivePaths = caseSensitive;
            builder.AddController<object>("GET", "/api/a%2Fb/details", null, (_, _) => ValueTask.FromResult<IActionResult>(new TextResult("encoded")));
        });
        await using var request = app.Request("http://localhost" + path);
        await app.Services.GetRequiredService<IRequestHandler>().HandleAsync(request.Context.Request, request.Response, CancellationToken.None);
        Assert.Equal(expected, request.Response.StatusCode);
    }

    [Theory]
    [InlineData("/api/products/current", "without")]
    [InlineData("/api/products/current/", "with")]
    public async Task StrictTrailingSlashRoutesRemainDistinct(string path, string expected)
    {
        await using var app = new SecurityTestApplication(builder =>
        {
            builder.Options.TrailingSlashes = TrailingSlashBehavior.Strict;
            builder.AddController<object>("GET", "/api/products/current", null, (_,  _) => ValueTask.FromResult<IActionResult>(new TextResult("without")));
            builder.AddController<object>("GET", "/api/products/current/", null, (_, _) => ValueTask.FromResult<IActionResult>(new TextResult("with")));
        });
        await using var request = app.Request("http://localhost" + path);
        await app.Services.GetRequiredService<IRequestHandler>().HandleAsync(request.Context.Request, request.Response, CancellationToken.None);
        Assert.Equal(expected, request.Response.Text);
    }

    [Fact]
    public async Task EquallySpecificMatchingConstraintsStillReportAmbiguity()
    {
        await using var app = new SecurityTestApplication(builder =>
        {
            builder.AddController<object>("GET", "/api/products/{id:min(1)}", null, (_,  _) => ValueTask.FromResult<IActionResult>(new TextResult("minimum")));
            builder.AddController<object>("GET", "/api/products/{id:max(10)}", null, (_, _) => ValueTask.FromResult<IActionResult>(new TextResult("maximum")));
        });
        await using var request = app.Request("http://localhost/api/products/5");
        await app.Services.GetRequiredService<IRequestHandler>().HandleAsync(request.Context.Request, request.Response, CancellationToken.None);
        Assert.Equal(StatusCodes.InternalServerError, request.Response.StatusCode);
    }

    [Fact]
    public void DeferredCapturesKeepDefaultsAndCatchAllsWithoutReevaluatingConstraints()
    {
        var calls   = 0;
        var options = new ServalOptions();
        options.RouteConstraints["probe"] = (_, _) =>
        {
            calls++;
            return true;
        };
        var template = new RouteTemplate("/api/items/{id:probe}/detail/{page:int=2}/{**tail}", options);
        Assert.True(template.TryMatch(["api", "items", "7", "detail"], false, out var values));
        Assert.Equal(1, calls);
        Assert.Equal("7", values["id"]);
        Assert.Equal("2", values["page"]);
        Assert.Equal("", values["tail"]);
        Assert.True(template.TryMatch(["api", "items", "8", "detail", "3", "a/b", "c"], false, out values));
        Assert.Equal(2, calls);
        Assert.Equal("8", values["id"]);
        Assert.Equal("3", values["page"]);
        Assert.Equal("a/b/c", values["tail"]);
    }

    private static SecurityTestApplication Application() => new(builder =>
    {
        builder.AddController<object>("GET", "/{**path}", null, (_,                      context) => ValueTask.FromResult<IActionResult>(new TextResult("root:" + context.RouteValues["path"])));
        builder.AddController<object>("GET", "/api/{**path}", null, (_,                  context) => ValueTask.FromResult<IActionResult>(new TextResult("api:"  + context.RouteValues["path"])));
        builder.AddController<object>("GET", "/api/{category}/{slug}", null, (_,         context) => ValueTask.FromResult<IActionResult>(new TextResult($"category:{context.RouteValues["category"]}:{context.RouteValues["slug"]}")));
        builder.AddController<object>("HEAD", "/api/{category}/{slug}", null, (_,        context) => ValueTask.FromResult<IActionResult>(new TextResult($"head:{context.RouteValues["category"]}:{context.RouteValues["slug"]}")));
        builder.AddController<object>("GET", "/api/products/{id:int}/details", null, (_, context) => ValueTask.FromResult<IActionResult>(new TextResult("product:" + context.RouteValues["id"])));
        builder.AddController<object>("GET", "/api/products/current", null, (_,          _) => ValueTask.FromResult<IActionResult>(new TextResult("current")));
        builder.AddController<object>("POST", "/api/products/current", null, (_,         _) => ValueTask.FromResult<IActionResult>(new TextResult("posted")));
    });
}
