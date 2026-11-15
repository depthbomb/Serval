#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Serval.Sample.BackgroundServices;

public sealed record CatalogEntry(string? Sku, string? Name, int PriceCents);

[JsonSerializable(typeof(CatalogEntry[]))]
[JsonSerializable(typeof(Product))]
[JsonSerializable(typeof(Product[]))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal partial class CatalogJsonContext : JsonSerializerContext;

public sealed class CatalogImporter
{
    private readonly TimeProvider             _clock;
    private readonly CatalogDbContext         _database;
    private readonly ILogger<CatalogImporter> _logger;
    private readonly IOptions<CatalogOptions> _options;
    private readonly CatalogImportState       _state;

    public CatalogImporter(CatalogDbContext database, IOptions<CatalogOptions> options, CatalogImportState state, TimeProvider clock, ILogger<CatalogImporter> logger)
    {
        _database = database;
        _options  = options;
        _state    = state;
        _clock    = clock;
        _logger   = logger;
    }

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        CatalogEntry?[]? entries;
        await using (var source = new FileStream(_options.Value.SourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            entries = await JsonSerializer.DeserializeAsync(source, CatalogJsonContext.Default.CatalogEntryArray, cancellationToken);
        }

        if (entries is null || entries.Length > 1000)
        {
            throw new InvalidDataException("The catalog must be an array of at most 1000 products.");
        }

        // Validate the whole source before changing tracked entities. A broken
        // edit must not replace the last good catalog with a partial import.
        var incoming = new Dictionary<string, CatalogEntry>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (entry is null                                                                                || string.IsNullOrWhiteSpace(entry.Sku) || entry.Sku.Length > 40 ||
                !entry.Sku.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_') ||
                string.IsNullOrWhiteSpace(entry.Name)                                                        || entry.Name.Length > 120 || entry.PriceCents < 0)
            {
                throw new InvalidDataException("Products need a unique SKU of 1-40 letters, digits, hyphens or underscores, a name of 1-120 characters, and a nonnegative price.");
            }

            if (!incoming.TryAdd(entry.Sku, entry))
            {
                throw new InvalidDataException($"The catalog contains duplicate SKU '{entry.Sku}'.");
            }
        }

        var existing = await _database.Products.ToDictionaryAsync(product => product.Sku, cancellationToken);
        foreach (var (sku, entry) in incoming)
        {
            if (!existing.Remove(sku, out var product))
            {
                product = new Product
                {
                    Sku = sku,
                };
                _database.Products.Add(product);
            }

            product.Name       = entry.Name!.Trim();
            product.PriceCents = entry.PriceCents;
        }

        // The source describes the entire catalog. One SaveChangesAsync commits
        // additions, updates, and removals together using EF's transaction.
        _database.Products.RemoveRange(existing.Values);
        await _database.SaveChangesAsync(cancellationToken);
        _state.Succeeded(_clock.GetUtcNow(), incoming.Count);

        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("Imported {ProductCount} catalog products.", incoming.Count);
        }
    }
}
