#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using System.Net.Http.Headers;

namespace Serval.Http.StaticFiles;

/// <summary>A physical file and a snapshot of its cache validators.</summary>
public sealed class FileResource
{
    /// <summary>The full physical path. The caller owns authorization for this file.</summary>
    public string Path { get; }
    /// <summary>The length at inspection time.</summary>
    public long Length { get; }
    /// <summary>The last modification time at inspection.</summary>
    public DateTimeOffset LastModified { get; }
    /// <summary>A weak metadata validator, or the validator supplied by the caller.</summary>
    public string ETag { get; }

    /// <summary>Inspects a file. Use StaticFileResolver first for untrusted URL paths.</summary>
    public FileResource(string path, string? etag = null)
    {
        if (etag is not null && (!EntityTagHeaderValue.TryParse(etag, out var parsed) || parsed.Tag == "*"))
        {
            throw new ArgumentException("An ETag must be a quoted entity tag, optionally prefixed with W/.", nameof(etag));
        }

        var info = new FileInfo(path);
        Path         = info.FullName;
        Length       = info.Length;
        LastModified = info.LastWriteTimeUtc;
        ETag         = etag ?? $"W/\"{Length:x}-{info.LastWriteTimeUtc.Ticks:x}\"";
    }
}
