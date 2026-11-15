#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Serval.Http.Constants;
using Serval.Sample.Vite.Generated;
using Serval.Tests.Support;

namespace Serval.Tests.Samples;

public sealed class ViteSampleTests
{
    [Fact]
    public async Task SampleRendersGeneratedReferencesAndServesTheirFiles()
    {
        await using var app  = new SampleApplication("Vite");
        var             page = await app.SendAsync("/");
        Assert.Equal(200, page.StatusCode);
        Assert.Contains("Serval.Vite", page.Text);
        Assert.False(Assets.IsDevelopment);
        Assert.DoesNotContain("@vite/client", page.Text);
        var entry = Assets.GetEntry("src/main.js");
        Assert.Single(entry.Scripts);
        Assert.NotEmpty(entry.Stylesheets);
        var logo = Assets.GetUrl("src/logo.svg");
        foreach (var url in entry.Scripts.Concat(entry.Stylesheets).Concat(entry.ModulePreloads).Append(logo))
        {
            Assert.Contains(url, page.Text);
            var asset = await app.SendAsync(url);
            Assert.Equal(200, asset.StatusCode);
            Assert.NotEmpty(asset.Text);
            Assert.Equal("public, max-age=31536000, immutable", asset.Headers[HeaderNames.CacheControl]);
        }

        var lazy = Assets.GetUrl("src/details.js");
        Assert.DoesNotContain(lazy, page.Text);
        Assert.DoesNotContain(lazy, entry.ModulePreloads);
        Assert.Equal(200, (await app.SendAsync(lazy)).StatusCode);
        Assert.Equal(404, (await app.SendAsync("/.vite/manifest.json")).StatusCode);
    }
}
