#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Serval.Http.Constants;

namespace Serval.Http.StaticFiles;

/// <summary>Chooses when static files are considered during request dispatch.</summary>
public enum StaticFilePriority
{
    /// <summary>Checks for an eligible public file before controller routes.</summary>
    BeforeRouting,

    /// <summary>Checks for an eligible public file only when routing did not handle the request.</summary>
    AfterRouting,
}

/// <summary>Configures optional static-file serving for GET and HEAD requests.</summary>
public sealed class StaticFileOptions
{
    /// <summary>
    ///     The existing directory to serve, or null to disable static files. No directory listing is provided.
    /// </summary>
    public string? DirectoryPath { get; set; }

    /// <summary>When regular file serving runs relative to routing. Defaults to AfterRouting.</summary>
    public StaticFilePriority Priority { get; set; } = StaticFilePriority.AfterRouting;

    /// <summary>
    ///     Whether unmatched GET and HEAD requests serve index.html from <see cref="DirectoryPath" />. Defaults to false.
    /// </summary>
    /// <remarks>
    ///     The fallback runs after routes and regular files, regardless of <see cref="Priority" />, and keeps the requested
    ///     URL unchanged so a client-side router can handle it. It applies to all unmatched paths, including missing asset
    ///     URLs. Controller responses and routing errors are preserved. The index file uses the usual static-file access
    ///     checks, cache headers, and conditional request handling. A missing or blocked index file leaves the request
    ///     unhandled; setting this option without <see cref="DirectoryPath" /> does not enable static files.
    /// </remarks>
    public bool FallbackToIndexHtml { get; set; }

    /// <summary>Whether unrecognized extensions are served as application/octet-stream. Defaults to false.</summary>
    public bool ServeUnknownFileTypes { get; set; }

    /// <summary>Whether dot-prefixed path segments and hidden files are allowed. Defaults to false.</summary>
    public bool ServeDotFiles { get; set; }

    /// <summary>
    ///     The Cache-Control value sent for static files. Defaults to public, max-age=0, must-revalidate.
    /// </summary>
    public string CacheControl { get; set; } = CacheControlDirectives.Public + ", " + CacheControlDirectives.MaxAge + "=0, " + CacheControlDirectives.MustRevalidate;

    internal void Validate()
    {
        if (!Enum.IsDefined(Priority))
        {
            throw new ArgumentOutOfRangeException(nameof(Priority));
        }

        if (DirectoryPath is not null && (string.IsNullOrWhiteSpace(DirectoryPath) || !Directory.Exists(DirectoryPath)))
        {
            throw new ArgumentException("The public-files directory must exist.", nameof(DirectoryPath));
        }

        _ = new Headers
        {
            [HeaderNames.CacheControl] = CacheControl
        };
    }
}
