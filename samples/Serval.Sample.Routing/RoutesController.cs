#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Serval.Binding;
using Serval.Generated;
using Serval.Http;
using Serval.Http.Constants;
using Serval.Routing;

namespace Serval.Sample.Routing;

[Controller]
public sealed class RoutesController : ControllerBase
{
    [Get("/")]
    public ValueTask<IActionResult> IndexAsync() => TextAsync("""
                                                              Typed URLs and parameter binding
                                                              GET /products/42             Bound int with a route constraint
                                                              GET /products/featured       Literal route takes precedence
                                                              GET /articles/hello-serval    Custom lowercase slug constraint
                                                              GET /archive or /archive/2026 Default from the route template
                                                              GET /hello or /hello/Reese    Optional value with a C# default
                                                              GET /tree/guides/first-steps  Catch-all route value
                                                              GET /search?page=2&tag=csharp&tag=http
                                                              GET /search                  Default page and an empty tag array
                                                              GET /links                   Typed URL helpers, defaults, and escaping
                                                              GET /request-info            Header bindings and an injected clock
                                                              GET /products/42/            Canonical redirect, preserving the query
                                                              HEAD /products/42 and OPTIONS /products/42 are automatic.

                                                              Try headers with:
                                                              curl.exe -H "X-Client: routing-demo" -H "X-Request-Id: 722bd98c-c9e3-4e24-a81b-c9c320349db3" http://localhost:8101/request-info
                                                              Missing headers use defaults; invalid GUIDs return 400.
                                                              See Serval.Sample.TodoApi for JSON body binding and generated JSON metadata.
                                                              """);

    [Get("/products/{id:int:min(1)}", Name = "products.show")]
    public ValueTask<IActionResult> ProductAsync([FromRoute] int id)
        => JsonAsync(new
        {
            id
        });

    [Get("/products/featured")]
    public ValueTask<IActionResult> FeaturedAsync() => TextAsync("The literal route wins.");

    [Get("/articles/{slug:slug}")]
    public ValueTask<IActionResult> ArticleAsync([FromRoute] string slug) => TextAsync(slug);

    [Get("/archive/{year:int:range(2000,2100)=2026}", Name = "archive.show")]
    public ValueTask<IActionResult> ArchiveAsync([FromRoute] int year)
        => JsonAsync(new
        {
            year
        });

    [Get("/hello/{name?}", Name = "hello.show")]
    public ValueTask<IActionResult> HelloAsync([FromRoute] string name = "visitor")
        => TextAsync($"Hello, {name}!");

    [Get("/tree/{**path}", Name = "tree.show")]
    public ValueTask<IActionResult> TreeAsync([FromRoute] string path)
        => JsonAsync(new
        {
            path
        });

    [Get("/search")]
    public ValueTask<IActionResult> SearchAsync([FromQuery("tag")] string[] tags, [FromQuery] int page = 1)
    {
        // Missing values use the parameter's default; business rules still belong in the action.
        AbortIf(page < 1, StatusCodes.BadRequest, "Page must be positive.");
        return JsonAsync(new
        {
            page,
            tags,
        });
    }

    [Get("/request-info")]
    public ValueTask<IActionResult> RequestInfoAsync([FromServices]               TimeProvider clock,
                                                     [FromHeader("X-Request-Id")] Guid?        requestId,
                                                     [FromHeader("X-Client")]     string       client = "browser") =>
        // The clock comes from DI for this action. No header means a null request ID.
        JsonAsync(new
        {
            requestId,
            client,
            utc = clock.GetUtcNow(),
        });

    [Get("/links")]
    public ValueTask<IActionResult> LinksAsync()
    {
        var product = Urls.ProductsShow(42, new Dictionary<string, object?>
        {
            ["ref"] = "samples & recipes",
        });
        var tree = Urls.TreeShow("guides/first steps");

        // Omitting a value keeps the route's default or leaves out its optional segment.
        var archive  = Urls.ArchiveShow();
        var hello    = Urls.HelloShow();
        var personal = Urls.HelloShow("Reese & friends");
        return JsonAsync(new
        {
            product,
            tree,
            archive,
            hello,
            personal,
        });
    }
}
