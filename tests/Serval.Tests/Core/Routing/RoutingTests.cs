#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.Extensions.DependencyInjection;
using Serval.Http;
using Serval.Http.Constants;
using Serval.Http.Results;
using Serval.Routing;
using Serval.Tests.Support;

namespace Serval.Tests.Core.Routing;

public sealed class RoutingTests
{
    [Fact]
    public async Task GeneratedControllersApplyClassAndMethodMiddleware()
    {
        await using var app      = new TestApplication();
        var             response = await app.SendAsync("/test/items/12");
        Assert.Equal(StatusCodes.Ok, response.StatusCode);
        Assert.Equal("12:controller,method", response.Text);
        Assert.Equal("task", (await app.SendAsync("/test/task")).Text);
    }

    [Theory]
    [InlineData("/test/items/0")]
    [InlineData("/test/items/no")]
    [InlineData("/test/items/2147483648")]
    [InlineData("/missing")]
    [InlineData("/test//items/1")]
    [InlineData("/test/task//")]
    [InlineData("/test/optional//")]
    public async Task ConstraintsAndMissingRoutesReturn404(string path)
    {
        await using var app = new TestApplication();
        Assert.Equal(StatusCodes.NotFound, (await app.SendAsync(path)).StatusCode);
    }

    [Fact]
    public async Task StaticRoutesDefaultsAndCatchAllsWork()
    {
        await using var app = new TestApplication();
        Assert.Equal("new", (await app.SendAsync("/test/items/new")).Text);
        Assert.Equal("1", (await app.SendAsync("/test/optional")).Text);
        Assert.Equal("2", (await app.SendAsync("/test/optional/2")).Text);
        Assert.Equal("a/b c", (await app.SendAsync("/test/files/a/b%20c")).Text);
        Assert.Equal("", (await app.SendAsync("/test/files")).Text);
    }

    [Theory]
    [InlineData(TrailingSlashBehavior.Ignore, "/test/items/1/", 200, null)]
    [InlineData(TrailingSlashBehavior.Strict, "/test/items/1/", 404, null)]
    [InlineData(TrailingSlashBehavior.RedirectToWithoutSlash, "/test/items/1/?a=b%20c", 308, "/test/items/1?a=b%20c")]
    [InlineData(TrailingSlashBehavior.RedirectToWithSlash, "/test/items/1?a=b%20c", 308, "/test/items/1/?a=b%20c")]
    [InlineData(TrailingSlashBehavior.RedirectToWithSlash, "//evil.example/path", 400, null)]
    public async Task SlashPoliciesPreserveQueryStrings(TrailingSlashBehavior behavior, string path, int status, string? location)
    {
        await using var app      = new TestApplication(options => options.TrailingSlashes = behavior);
        var             response = await app.SendAsync(path);
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(location, response.Headers[HeaderNames.Location]);
    }

    [Theory]
    [InlineData(false, 200)]
    [InlineData(true, 404)]
    public async Task CaseSensitivityIsConfigurable(bool sensitive, int status)
    {
        await using var app = new TestApplication(options => options.CaseSensitivePaths = sensitive);
        Assert.Equal(status, (await app.SendAsync("/TEST/items/1")).StatusCode);
    }

    [Fact]
    public async Task ReverseRoutesValidateAndEscapeValues()
    {
        await using var app  = new TestApplication(options => options.TrailingSlashes = TrailingSlashBehavior.RedirectToWithSlash);
        var             urls = app.Services.GetRequiredService<IUrlGenerator>();
        Assert.Equal("/test/items/12/?q=a%20b", urls.Path("item", new Dictionary<string, object?>
        {
            ["id"] = 12,
        }, new Dictionary<string, object?>
        {
            ["q"] = "a b",
        }));
        Assert.Equal("/test/files/a/b%20c/", urls.Path("files", new Dictionary<string, object?>
        {
            ["path"] = "a/b c",
        }));
        Assert.Throws<ArgumentException>(() => urls.Path("item", new Dictionary<string, object?>
        {
            ["id"] = 0,
        }));
        Assert.Throws<ArgumentException>(() => urls.Path("item"));
        Assert.Throws<ArgumentException>(() => urls.Path("missing"));
    }

    [Fact]
    public async Task MethodNegotiationReturnsAllow()
    {
        await using var app      = new TestApplication();
        var             rejected = await app.SendAsync("/test/items/1", HttpMethods.Post);
        Assert.Equal(StatusCodes.MethodNotAllowed, rejected.StatusCode);
        Assert.Equal("GET, HEAD, OPTIONS", rejected.Headers[HeaderNames.Allow]);
        var options = await app.SendAsync("/test/items/1", HttpMethods.Options);
        Assert.Equal(StatusCodes.NoContent, options.StatusCode);
        Assert.Equal("GET, HEAD, OPTIONS", options.Headers[HeaderNames.Allow]);
    }

    [Fact]
    public void DuplicateRoutesFailDuringRouterConstruction()
    {
        var registry = new ControllerRegistry();
        registry.Add<TestController>(HttpMethods.Get, "/a/{id:int}", "one", (_,    _) => ValueTask.FromResult<IActionResult>(EmptyResult.Instance));
        registry.Add<TestController>(HttpMethods.Get, "/a/{value:int}", "two", (_, _) => ValueTask.FromResult<IActionResult>(EmptyResult.Instance));
        Assert.Throws<InvalidOperationException>(() => new Router(registry, new ServalOptions()));
    }

    [Theory]
    [InlineData("guid", "722bd98c-c9e3-4e24-a81b-c9c320349db3", true)]
    [InlineData("guid", "no", false)]
    [InlineData("alpha", "abc", true)]
    [InlineData("alpha", "123", false)]
    [InlineData("range(1,10)", "5", true)]
    [InlineData("range(1,10)", "11", false)]
    [InlineData("length(2,4)", "abc", true)]
    [InlineData("length(2,4)", "a", false)]
    [InlineData("regex(^[a-z]{2}$)", "ab", true)]
    [InlineData("regex(^[a-z]{2}$)", "abc", false)]
    public void ConstraintsUseInvariantParsing(string constraint, string value, bool expected)
    {
        Assert.Equal(expected, new RouteConstraint(constraint, new ServalOptions()).Matches(value));
    }

    [Fact]
    public void CustomConstraintsAndChainedDefaultsAreValidated()
    {
        var options = new ServalOptions
        {
            RouteConstraints =
            {
                ["slug"] = (value, _) => value.All(c => char.IsAsciiLetterLower(c) || c == '-'),
            },
        };
        var slug = new RouteTemplate("/blog/{name:slug}", options);
        Assert.True(slug.TryMatch(["blog", "hello-world"], false, out _));
        Assert.False(slug.TryMatch(["blog", "Hello"], false, out _));
        var defaults = new RouteTemplate("/page/{number:int:min(1)=2}", options);
        Assert.True(defaults.TryMatch(["page"], false, out var values));
        Assert.Equal("2", values["number"]);
        Assert.Throws<ArgumentException>(() => new RouteTemplate("/page/{number:int:min(1)=0}", options));
    }
}
