#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using System.Buffers;

namespace Serval.Http.StaticFiles;

/// <summary>Resolves URL paths below one public directory, rejecting traversal, links, and hidden paths by default.</summary>
/// <remarks>The directory must be application-controlled. Filesystem checks cannot prevent another writer replacing entries between resolution and opening.</remarks>
public sealed class StaticFileResolver
{
    private readonly string _root;
    private readonly bool   _serveDotFiles;
    private readonly bool   _ignoreTrailingSlash;

    private static readonly SearchValues<char> InvalidFileNameCharacters = SearchValues.Create(Path.GetInvalidFileNameChars());

    /// <summary>Creates a resolver for an existing directory.</summary>
    public StaticFileResolver(string directory, bool serveDotFiles = false, bool ignoreTrailingSlash = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        if (!Directory.Exists(directory))
        {
            throw new ArgumentException("The public-files directory must exist.", nameof(directory));
        }

        var info = new DirectoryInfo(Path.GetFullPath(directory));
        _root                = Path.TrimEndingDirectorySeparator(info.ResolveLinkTarget(true)?.FullName ?? info.FullName);
        _serveDotFiles       = serveDotFiles;
        _ignoreTrailingSlash = ignoreTrailingSlash;
    }

    /// <summary>Returns an eligible physical file path, or null when absent or blocked. Does not open the file.</summary>
    public string? Resolve(string requestPath)
    {
        ArgumentNullException.ThrowIfNull(requestPath);
        var segments = requestPath.TrimStart('/').Split('/').Select(Uri.UnescapeDataString).ToArray();
        if (segments[^1].Length == 0 && _ignoreTrailingSlash)
        {
            segments = segments[..^1];
        }

        if (segments.Length == 0)
        {
            return null;
        }

        var current = _root;
        try
        {
            for (var index = 0; index < segments.Length; index++)
            {
                var segment = segments[index];
                var invalid = segment.Length == 0                                     ||
                              segment is "." or ".."                                  ||
                              segment.AsSpan().ContainsAny(InvalidFileNameCharacters) ||
                              segment.AsSpan().ContainsAnyInRange('\0', '\x1f')       ||
                              segment.AsSpan().ContainsAny('/', '\\', ':');

                // Win32 strips trailing dots and spaces, which can turn an allowed segment into '..'.
                invalid |= OperatingSystem.IsWindows() && (segment.EndsWith('.') || segment.EndsWith(' '));
                if (invalid || !_serveDotFiles && segment.StartsWith('.'))
                {
                    return null;
                }

                current = Path.Combine(current, segment);
                if (OperatingSystem.IsWindows() && segment.Contains('~'))
                {
                    // Expand DOS aliases before applying name and extension policies to the physical file.
                    current = Path.GetFullPath(current);
                    if (!_serveDotFiles && Path.GetFileName(current).StartsWith('.'))
                    {
                        return null;
                    }
                }

                var attributes = File.GetAttributes(current);
                // Check every segment: a link in an intermediate directory could escape the public root.
                if ((attributes & FileAttributes.ReparsePoint) != 0 || !_serveDotFiles && (attributes & FileAttributes.Hidden) != 0)
                {
                    return null;
                }

                var directory = (attributes & FileAttributes.Directory) != 0;
                if (directory != index < segments.Length - 1)
                {
                    return null;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }

        return current;
    }
}
