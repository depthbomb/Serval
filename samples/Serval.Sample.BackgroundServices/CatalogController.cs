#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Serval.Http;
using Serval.Http.Constants;
using Serval.Routing;

namespace Serval.Sample.BackgroundServices;

[Controller]
public sealed class CatalogController : ControllerBase
{
    private readonly CatalogDbContext   _database;
    private readonly IHostEnvironment   _environment;
    private readonly CatalogImportState _state;

    public CatalogController(CatalogDbContext database, CatalogImportState state, IHostEnvironment environment)
    {
        _database    = database;
        _state       = state;
        _environment = environment;
    }

    [Get("/")]
    public ValueTask<IActionResult> IndexAsync(RequestContext context) => TextAsync("""
                                                                                    Hosted initialization + scoped background work + EF Core
                                                                                    GET /catalog            Read the latest imported products.
                                                                                    GET /catalog/coffee     Read one product.
                                                                                    GET /status             See the host environment and refresh status.
                                                                                    Edit catalog-source.json beside the executable to change the catalog.
                                                                                    The source is authoritative: omitted products are removed; [] clears it.
                                                                                    Invalid edits keep the previous catalog available and retry next interval.
                                                                                    Configure --Catalog:SourcePath "your-file.json" --Catalog:RefreshInterval "00:00:05".
                                                                                    Configure --ConnectionStrings:Catalog "Data Source=your-cache.db" and --port 8112.
                                                                                    Ctrl+C stops the HTTP listener and cancels the background worker.
                                                                                    """);

    [Get("/catalog")]
    public async ValueTask<IActionResult> ListAsync(RequestContext context)
    {
        var products = await _database.Products.AsNoTracking().OrderBy(product => product.Sku).Take(1000).ToArrayAsync(context.RequestAborted);
        return Json(products, CatalogJsonContext.Default.ProductArray);
    }

    [Get("/catalog/{sku}")]
    public async ValueTask<IActionResult> GetAsync(RequestContext context)
    {
        var sku     = context.RouteValues["sku"];
        var product = await _database.Products.AsNoTracking().SingleOrDefaultAsync(product => product.Sku == sku, context.RequestAborted);
        AbortIf(product is null, StatusCodes.NotFound, "Product not found.");
        return Json(product, CatalogJsonContext.Default.Product);
    }

    [Get("/status")]
    public ValueTask<IActionResult> StatusAsync(RequestContext context) => JsonAsync(new
    {
        application = _environment.ApplicationName,
        environment = _environment.EnvironmentName,
        import      = _state.Current,
    });
}
