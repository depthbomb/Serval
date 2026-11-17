#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.EntityFrameworkCore;

namespace Serval.Sample.EntityFramework;

public sealed class Note
{
    public int    Id    { get; set; }
    public string Title { get; set; } = "";
    public string Body  { get; set; } = "";
}

public sealed class NotesDbContext : DbContext
{
    public DbSet<Note> Notes => Set<Note>();

    public NotesDbContext(DbContextOptions<NotesDbContext> options) : base(options) {}

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Note>().Property(note => note.Title).HasMaxLength(120).IsRequired();
        modelBuilder.Entity<Note>().Property(note => note.Body).HasMaxLength(4000).IsRequired();
    }
}
