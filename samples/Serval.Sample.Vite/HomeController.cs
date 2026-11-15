#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Serval.Http;
using Serval.Savanna;
using Serval.Routing;
using Serval.Sample.Vite.Generated;
using Serval.Vite;

namespace Serval.Sample.Vite;

public sealed class HomeModel
{
    public ViteEntry Entry         { get; }
    public string    LogoUrl       { get; }
    public bool      IsDevelopment { get; }

    public HomeModel(ViteEntry entry, string logoUrl, bool isDevelopment)
    {
        Entry         = entry;
        LogoUrl       = logoUrl;
        IsDevelopment = isDevelopment;
    }
}

[Controller]
public sealed class HomeController
{
    [Get("/")]
    public ValueTask<IActionResult> IndexAsync(RequestContext context)
    {
        // Both modes use generated lookups. Development points at Vite; production points at the built files.
        var model = new HomeModel(Assets.GetEntry("src/main.js"), Assets.GetUrl("src/logo.svg"), Assets.IsDevelopment);
        return context.ViewAsync(Views.Index, model);
    }
}
