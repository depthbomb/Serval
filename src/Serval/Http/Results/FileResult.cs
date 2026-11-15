#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

namespace Serval.Http.Results;

/// <summary>Defers opening and streaming a file until result execution.</summary>
public sealed class FileResult : IActionResult
{
    /// <summary>The file path opened during execution.</summary>
    public string Path { get; }

    /// <summary>The supplied media type, or null to use application/octet-stream.</summary>
    public string? ContentType { get; }

    /// <summary>The optional filename for the Content-Disposition attachment header.</summary>
    public string? DownloadName { get; }

    /// <summary>Creates a deferred file result with optional content type and download name.</summary>
    /// <param name="path">The file path to open.</param>
    /// <param name="contentType">The media type, or null for application/octet-stream.</param>
    /// <param name="downloadName">An optional attachment filename.</param>
    public FileResult(string path, string? contentType = null, string? downloadName = null)
    {
        Path         = path;
        ContentType  = contentType;
        DownloadName = downloadName;
    }

    /// <inheritdoc />
    public ValueTask ExecuteAsync(RequestContext context)
        => context.Response.WriteFileAsync(Path, ContentType, DownloadName, context.RequestAborted);
}
