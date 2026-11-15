#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using System.Text.Json;
using Serval.Savanna.LanguageServer.Workspace;

namespace Serval.Savanna.LanguageServer.Tests;

public sealed class WorkspaceCacheTests
{
    [Fact]
    public async Task TemplateSnapshotsFollowEditsClosesCreatesDeletesAndWatcherInvalidation()
    {
        var root = Path.Combine(Path.GetTempPath(), "SavannaWorkspaceTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "Views"));
        var main    = Path.Combine(root, "Main.svn");
        var partial = Path.Combine(root, "Views", "Card.svn");
        await File.WriteAllTextAsync(main, "{% model string %}main");
        await File.WriteAllTextAsync(partial, "{% model string %}disk");
        using var workspace = new SavannaWorkspace();
        var       initial   = await workspace.SnapshotAsync(main, CancellationToken.None);
        var       repeat    = await workspace.SnapshotAsync(main, CancellationToken.None);
        Assert.Same(initial.Document, repeat.Document);
        Assert.All(initial.Templates, document => Assert.Same(document, repeat.Templates.Single(candidate => candidate.Path == document.Path)));

        workspace.Open(partial, "{% model int %}unsaved", 1);
        Assert.Equal("int", (await workspace.SnapshotAsync(partial, CancellationToken.None)).Document.Model);
        workspace.Change(partial, JsonSerializer.SerializeToElement(new[] { new { text = "{% model bool %}changed" } }), 2);
        Assert.Equal("bool", (await workspace.SnapshotAsync(partial, CancellationToken.None)).Document.Model);
        Assert.False(workspace.Change(partial, JsonSerializer.SerializeToElement(new[] { new { text = "stale" } }), 1));
        workspace.Close(partial);
        Assert.Equal("string", (await workspace.SnapshotAsync(partial, CancellationToken.None)).Document.Model);

        // A watched replacement can retain both its timestamp and length.
        var modified = File.GetLastWriteTimeUtc(partial);
        await File.WriteAllTextAsync(partial, "{% model string %}edit");
        File.SetLastWriteTimeUtc(partial, modified);
        workspace.InvalidateTemplates([partial]);
        var changed = await workspace.SnapshotAsync(main, CancellationToken.None);
        Assert.EndsWith("edit", changed.Templates.Single(document => document.Path == partial).Text.ToString());

        var created = Path.Combine(root, "Views", "New.svn");
        await File.WriteAllTextAsync(created, "{% model int %}new");
        Assert.Contains((await workspace.SnapshotAsync(main, CancellationToken.None)).Templates, document => document.Path == created);
        File.Delete(created);
        Assert.DoesNotContain((await workspace.SnapshotAsync(main, CancellationToken.None)).Templates, document => document.Path == created);
        await File.WriteAllTextAsync(partial, "{% model decimal %}longer");
        Assert.Equal("decimal", (await workspace.SnapshotAsync(main, CancellationToken.None)).Templates.Single(document => document.Path == partial).Model);
    }
}
