#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

namespace Serval.Vite.Generators;

internal sealed class EntryAssets
{
    public string[] Scripts        { get; }
    public string[] Stylesheets    { get; }
    public string[] ModulePreloads { get; }

    public EntryAssets(string[] scripts, string[] stylesheets, string[] modulePreloads)
    {
        Scripts        = scripts;
        Stylesheets    = stylesheets;
        ModulePreloads = modulePreloads;
    }
}

internal static class ViteEntryBuilder
{
    public static EntryAssets Build(IReadOnlyDictionary<string, ManifestChunk> chunks,
                                    string                                     key,
                                    string                                     urlPrefix,
                                    Dictionary<string, string>?                resolvedUrls,
                                    CancellationToken                          cancellationToken)
    {
        var chunk      = chunks[key];
        var entryUrl   = ResolveUrl(urlPrefix, chunk.File, resolvedUrls);
        var scripts    = IsCss(chunk.File) ? Array.Empty<string>() : new[] { entryUrl };
        var css        = new List<string>();
        var cssSet     = new HashSet<string>(StringComparer.Ordinal);
        var preloads   = new List<string>();
        var preloadSet = new HashSet<string>(StringComparer.Ordinal);
        if (IsCss(chunk.File))
        {
            AddUnique(css, cssSet, entryUrl);
        }

        foreach (var file in chunk.Css)
        {
            AddUnique(css, cssSet, ResolveUrl(urlPrefix, file, resolvedUrls));
        }

        // An explicit stack handles deep graphs and cycles without using the compiler's call stack.
        // Each entry gets its own visited set: another page's entry can still be this page's dependency.
        var visited = new HashSet<string>(StringComparer.Ordinal)
        {
            key
        };
        var stack = new Stack<(string Key, bool Expanded)>();
        PushImports(stack, chunk);
        while (stack.Count != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var next       = stack.Pop();
            var dependency = chunks[next.Key];
            if (!next.Expanded)
            {
                if (!visited.Add(next.Key))
                {
                    continue;
                }

                // Postorder puts a module's dependencies before the module that imports them.
                stack.Push((next.Key, true));
                PushImports(stack, dependency);
                continue;
            }

            var url = ResolveUrl(urlPrefix, dependency.File, resolvedUrls);
            if (IsCss(dependency.File))
            {
                AddUnique(css, cssSet, url);
            }
            else if (url != entryUrl)
            {
                AddUnique(preloads, preloadSet, url);
            }

            foreach (var file in dependency.Css)
            {
                AddUnique(css, cssSet, ResolveUrl(urlPrefix, file, resolvedUrls));
            }
        }

        // cssCodeSplit: false moves styles into this standalone output instead of each chunk's css list.
        // A separately configured CSS entry can also be called style.css; it belongs to its own page.
        if (chunks.TryGetValue("style.css", out var globalCss) && !globalCss.IsEntry && IsCss(globalCss.File))
        {
            AddUnique(css, cssSet, ResolveUrl(urlPrefix, globalCss.File, resolvedUrls));
        }

        return new EntryAssets(scripts, [.. css], [.. preloads]);
    }

    public static string ResolveUrl(string prefix, string file, Dictionary<string, string>? resolvedUrls)
    {
        if (resolvedUrls is not null && resolvedUrls.TryGetValue(file, out var cached))
        {
            return cached;
        }

        // netstandard2.0's TrimStart overload allocates a params array, even for the usual unprefixed filename.
        var start = 0;
        while (start < file.Length && file[start] == '/')
        {
            start++;
        }

        var url = prefix + (start == 0 ? file : file.Substring(start));
        resolvedUrls?.Add(file, url);
        return url;
    }

    private static bool IsCss(string file) => file.EndsWith(".css", StringComparison.OrdinalIgnoreCase);

    private static void PushImports(Stack<(string Key, bool Expanded)> stack, ManifestChunk chunk)
    {
        for (var index = chunk.Imports.Length - 1; index >= 0; index--)
        {
            stack.Push((chunk.Imports[index], false));
        }
    }

    private static void AddUnique(List<string> list, HashSet<string> seen, string value)
    {
        if (seen.Add(value))
        {
            list.Add(value);
        }
    }
}
