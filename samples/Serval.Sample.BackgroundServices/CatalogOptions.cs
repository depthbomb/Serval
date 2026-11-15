#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

namespace Serval.Sample.BackgroundServices;

public sealed class CatalogOptions
{
    public string   SourcePath      { get; set; } = Path.Combine(AppContext.BaseDirectory, "catalog-source.json");
    public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromSeconds(5);
}
