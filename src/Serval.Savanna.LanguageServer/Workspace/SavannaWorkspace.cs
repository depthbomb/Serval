#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;
using System.Text.Json;
using Serval.Savanna.Compiler.Analysis;
using Serval.Savanna.Compiler.Parsing;
using Serval.Savanna.Compiler.Syntax;
using Serval.Savanna.LanguageServer.Features;
using Serval.Savanna.LanguageServer.Rpc;

namespace Serval.Savanna.LanguageServer.Workspace;

public sealed record OpenDocument(string Path, SourceText Text, int Version);

public sealed record TemplateSnapshot(SavannaDocument Document, IReadOnlyList<SavannaDocument> Templates, Project? Project, string? ProjectError = null);

public sealed class SavannaWorkspace : IDisposable
{
    public           IReadOnlyCollection<OpenDocument>        OpenDocuments => _open.Values;
    private          SavannaDocument[]                           _importFiles     = [];
    private readonly Dictionary<string, EditorAnalyzerLoader> _analyzerLoaders = new(PathComparer);
    private readonly Dictionary<string, Project>              _editorProjects  = new(PathComparer);
    private readonly Dictionary<string, CachedFeatures>       _features        = new(PathComparer);
    private readonly Dictionary<SavannaDocument, SavannaDocument>   _imported        = new();

    private readonly        Dictionary<string, OpenDocument>     _open          = new(PathComparer);
    private readonly        Dictionary<string, string>           _projectErrors = new(PathComparer);
    private readonly        Dictionary<string, MSBuildWorkspace> _projects      = new(PathComparer);
    private readonly        Dictionary<string, CachedTemplate>   _templates     = new(PathComparer);
    private static readonly StringComparer                       PathComparer   = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public void Dispose() => InvalidateProjects();

    public bool Open(string path, string text, int version)
    {
        if (_open.TryGetValue(path, out var current) && version <= current.Version)
        {
            return false;
        }

        _open[path] = new OpenDocument(path, SourceText.From(text), version);
        return true;
    }

    public bool Change(string path, JsonElement changes, int version)
    {
        if (!_open.TryGetValue(path, out var current) || version <= current.Version)
        {
            return false;
        }

        var text = current.Text;
        foreach (var change in changes.EnumerateArray())
        {
            var newText = change.GetRequiredString("text");
            if (change.TryGetProperty("range", out var range))
            {
                var start = Position.Read(range.GetProperty("start")).Offset(text);
                var end   = Position.Read(range.GetProperty("end")).Offset(text);
                text = text.WithChanges(new TextChange(TextSpan.FromBounds(start, end), newText));
            }
            else
            {
                text = SourceText.From(newText);
            }
        }

        _open[path] = current with { Text = text, Version = version };
        return true;
    }

    public void Close(string path)
    {
        _open.Remove(path);
        _templates.Remove(path);
        _features.Remove(path);
    }

    public void InvalidateTemplates(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            _templates.Remove(path);
            _features.Remove(path);
        }
    }

    public CSharpFeatures? GetCSharpFeatures(TemplateSnapshot snapshot)
    {
        if (snapshot.Project is null)
        {
            return null;
        }

        var path = snapshot.Document.Path;
        if (_features.TryGetValue(path, out var cached) && ReferenceEquals(cached.Document, snapshot.Document) && ReferenceEquals(cached.Project, snapshot.Project))
        {
            return cached.Features;
        }

        var features = new CSharpFeatures(snapshot);
        _features[path] = new CachedFeatures(snapshot.Document, snapshot.Project, features);
        return features;
    }

    public void InvalidateProjects()
    {
        foreach (var workspace in _projects.Values)
        {
            workspace.Dispose();
        }

        _projects.Clear();
        _editorProjects.Clear();
        foreach (var loader in _analyzerLoaders.Values)
        {
            loader.Dispose();
        }

        _analyzerLoaders.Clear();
        _projectErrors.Clear();
        _templates.Clear();
        _features.Clear();
        _imported.Clear();
        _importFiles = [];
    }

    public async Task<TemplateSnapshot> SnapshotAsync(string path, CancellationToken cancellationToken)
    {
        var      projectPath  = FindProject(path);
        Project? project      = null;
        string?  projectError = null;
        if (projectPath is not null)
        {
            if (!_projects.TryGetValue(projectPath, out var workspace) && !_projectErrors.ContainsKey(projectPath))
            {
                workspace = MSBuildWorkspace.Create();
                workspace.RegisterWorkspaceFailedHandler(eventArgs => Console.Error.WriteLine(eventArgs.Diagnostic.Message));
                try
                {
                    var loaded   = await workspace.OpenProjectAsync(projectPath, cancellationToken: cancellationToken);
                    var loader   = new EditorAnalyzerLoader();
                    var solution = loaded.Solution;
                    var projects = solution.Projects.ToArray();
                    foreach (var analyzer in projects.SelectMany(candidate => candidate.AnalyzerReferences).OfType<AnalyzerFileReference>())
                    {
                        loader.AddDependencyLocation(analyzer.FullPath);
                    }

                    foreach (var candidate in projects)
                    {
                        solution = solution.WithProjectAnalyzerReferences(candidate.Id, candidate.AnalyzerReferences.Select(reference =>
                            reference is AnalyzerFileReference file ? new AnalyzerFileReference(file.FullPath, loader) : reference));
                    }

                    _analyzerLoaders.Add(projectPath, loader);
                    _editorProjects.Add(projectPath, solution.GetProject(loaded.Id)!);
                    _projects.Add(projectPath, workspace);
                }
                catch (OperationCanceledException)
                {
                    workspace.Dispose();
                    throw;
                }
                catch (Exception error) when (error is IOException or InvalidOperationException or NotSupportedException)
                {
                    workspace.Dispose();
                    _projectErrors[projectPath] = "C# project loading failed: " + error.Message;
                }
            }

            _editorProjects.TryGetValue(projectPath, out project);
            _projectErrors.TryGetValue(projectPath, out projectError);
        }

        var projectDirectory = Path.GetDirectoryName(projectPath ?? path)!;
        var root             = Path.Combine(projectDirectory, "Views");
        if (project?.AnalyzerOptions.AnalyzerConfigOptionsProvider.GlobalOptions.TryGetValue("build_property.SavannaRoot", out var configuredRoot) == true && !string.IsNullOrWhiteSpace(configuredRoot))
        {
            root = Path.GetFullPath(configuredRoot, projectDirectory);
        }

        var paths = new HashSet<string>(PathComparer);
        if (Directory.Exists(root))
        {
            var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true };
            paths.UnionWith(Directory.EnumerateFiles(root, "*.svn", options));
        }

        if (project is not null)
        {
            paths.UnionWith(project.AdditionalDocuments.Select(document => document.FilePath).OfType<string>().Where(file => file.EndsWith(".svn", StringComparison.OrdinalIgnoreCase)));
        }

        paths.UnionWith(_open.Keys.Where(file => IsWithin(root, file)));
        paths.Add(path);
        foreach (var removed in _templates.Keys.Where(file => IsWithin(root, file) && !paths.Contains(file)).ToArray())
        {
            _templates.Remove(removed);
            _features.Remove(removed);
        }

        var documents = new List<SavannaDocument>();
        foreach (var file in paths.Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = IsWithin(root, file) ? Path.GetRelativePath(root, file).Replace('\\', '/') : Path.GetFileName(file);
            _templates.TryGetValue(file, out var cached);
            SourceText text;
            long       length   = 0;
            DateTime   modified = default;
            if (_open.TryGetValue(file, out var open))
            {
                if (cached?.Name == name && ReferenceEquals(cached.OpenText, open.Text))
                {
                    documents.Add(cached.Document);
                    continue;
                }

                text = open.Text;
            }
            else
            {
                try
                {
                    var info = new FileInfo(file);
                    length   = info.Length;
                    modified = info.LastWriteTimeUtc;
                    if (cached?.Name == name && cached.OpenText is null && cached.Length == length && cached.Modified == modified)
                    {
                        documents.Add(cached.Document);
                        continue;
                    }

                    text = SourceText.From(await File.ReadAllTextAsync(file, cancellationToken));
                }
                catch (IOException)
                {
                    _templates.Remove(file);
                    _features.Remove(file);
                    continue;
                }
            }

            var document = SavannaParser.Parse(file, name, text, cancellationToken);
            _templates[file] = new CachedTemplate(name, open?.Text, length, modified, document);
            documents.Add(document);
        }

        var importFiles = documents.Where(document => document.IsImports).ToArray();
        if (!_importFiles.SequenceEqual(importFiles))
        {
            _imported.Clear();
            _importFiles = importFiles;
        }

        foreach (var removed in _imported.Keys.Except(documents).ToArray())
        {
            _imported.Remove(removed);
        }

        var imported = documents.Select(document =>
        {
            if (_imported.TryGetValue(document, out var cached))
            {
                return cached;
            }

            var resolved = SavannaImports.Apply([.. importFiles.Append(document).Distinct()]).Single(value => value.Path == document.Path);
            _imported[document] = resolved;
            return resolved;
        }).ToArray();
        return new TemplateSnapshot(imported.Single(document => PathComparer.Equals(document.Path, path)), imported, project, projectError);
    }

    private static bool IsWithin(string root, string path) => Path.GetFullPath(path).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static string? FindProject(string path)
    {
        for (var directory = Path.GetDirectoryName(path); directory is not null; directory = Path.GetDirectoryName(directory))
        {
            var candidates = Directory.GetFiles(directory, "*.csproj");
            if (candidates.Length == 1)
            {
                return candidates[0];
            }

            if (candidates.Length > 1)
            {
                return null;
            }
        }

        return null;
    }

    private sealed record CachedTemplate(string Name, SourceText? OpenText, long Length, DateTime Modified, SavannaDocument Document);

    private sealed record CachedFeatures(SavannaDocument Document, Project Project, CSharpFeatures Features);
}
