#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.EntityFrameworkCore;

namespace Serval.Sample.BackgroundServices;

public sealed class Product
{
    public string Sku        { get; set; } = "";
    public string Name       { get; set; } = "";
    public int    PriceCents { get; set; }
}

public sealed class CatalogDbContext : DbContext
{
    public DbSet<Product> Products => Set<Product>();

    public CatalogDbContext(DbContextOptions<CatalogDbContext> options) : base(options) {}

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Product>().HasKey(product => product.Sku);
        modelBuilder.Entity<Product>().Property(product => product.Sku).HasMaxLength(40);
        modelBuilder.Entity<Product>().Property(product => product.Name).HasMaxLength(120).IsRequired();
    }
}
