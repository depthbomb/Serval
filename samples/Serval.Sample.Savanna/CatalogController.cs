#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Serval.Http;
using Serval.Savanna;
using Serval.Routing;
using Serval.Sample.Savanna.Generated;

namespace Serval.Sample.Savanna;

public sealed record SiteModel(string Name);

public sealed record CatalogModel(string Title, SiteModel Site);

public sealed record Product(string Name, decimal Price, string Url);

public sealed record ProductIndexModel(string Title, CatalogModel Catalog, IReadOnlyList<Product> Products);

[Controller]
public sealed class CatalogController
{
    [Get("/")]
    public ValueTask<IActionResult> IndexAsync(RequestContext context)
    {
        var model = new ProductIndexModel("Field notes", new CatalogModel("Catalog", new SiteModel("Serval.Savanna")),
        [
            new Product("Notebook <A5>", 12.50m, "/products/notebook"),
            new Product("Ink & pen", 8m, "/products/pen"),
        ]);
        return context.ViewAsync(Views.Products.Index, model);
    }
}
