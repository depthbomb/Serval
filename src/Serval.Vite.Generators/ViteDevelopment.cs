#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using System.Text;

namespace Serval.Vite.Generators;

internal static class ViteDevelopment
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

    public static Dictionary<string, ManifestChunk> Create(string serverUrl, string entries, string assets, CancellationToken cancellationToken)
    {
        var relative = serverUrl.StartsWith("/", StringComparison.Ordinal) && !serverUrl.StartsWith("//", StringComparison.Ordinal);
        var absolute = Uri.TryCreate(serverUrl, UriKind.Absolute, out var uri)               &&
                       (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) && uri.UserInfo.Length == 0;
        if (!relative && !absolute || serverUrl.IndexOfAny(['?', '#', '\\']) >= 0 || serverUrl.Any(char.IsWhiteSpace) || serverUrl.Any(char.IsControl))
        {
            throw new ArgumentException("ViteDevServerUrl must be a root-relative path or an HTTP(S) URL without credentials, a query, or a fragment.");
        }

        // Counting with a strict encoder rejects unpaired surrogates before URL handling can replace them.
        _ = StrictUtf8.GetByteCount(serverUrl);
        var chunks = new Dictionary<string, ManifestChunk>(StringComparer.Ordinal);
        AddPaths(entries, true);
        if (chunks.Count == 0)
        {
            throw new ArgumentException("Set ViteDevEntries to the source entry paths relative to Vite's root, separated by semicolons (for example src/main.js).");
        }

        AddPaths(assets, false);
        return chunks;

        void AddPaths(string paths, bool isEntry)
        {
            foreach (var item in paths.Split(';'))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var key = item.Trim();
                if (key.Length == 0)
                {
                    continue;
                }

                var segments = key.Split('/');
                if (key.IndexOf('\\') >= 0 || key.Any(char.IsControl) || segments.Any(segment => segment is "" or "." or ".."))
                {
                    throw new ArgumentException($"Vite development paths must be relative to Vite's root, with forward slashes and no dot segments: '{key}'.");
                }

                // Paths are filenames, not ready-made URLs. Escape each segment so spaces, # and % survive a request.
                _ = StrictUtf8.GetByteCount(key);
                var file = string.Join("/", segments.Select(Uri.EscapeDataString));
                if (!chunks.ContainsKey(key))
                {
                    chunks.Add(key, new ManifestChunk(file, isEntry, [], [], []));
                }
            }
        }
    }
}
