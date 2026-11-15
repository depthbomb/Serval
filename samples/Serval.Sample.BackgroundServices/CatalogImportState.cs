#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

namespace Serval.Sample.BackgroundServices;

public sealed record CatalogImportSnapshot(DateTimeOffset? LastSuccess, int ProductCount, string? LastError);

public sealed class CatalogImportState
{
    public  CatalogImportSnapshot Current => Volatile.Read(ref _current);
    private CatalogImportSnapshot _current = new(null, 0, null);

    public void Succeeded(DateTimeOffset at, int count) => Volatile.Write(ref _current, new CatalogImportSnapshot(at, count, null));

    public void Failed() => Volatile.Write(ref _current, Current with
    {
        LastError = "The latest refresh failed; the previous catalog is still available.",
    });
}
