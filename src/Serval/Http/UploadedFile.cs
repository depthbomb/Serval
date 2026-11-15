#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

namespace Serval.Http;

/// <summary>A multipart file buffered for the lifetime of its request.</summary>
public sealed class UploadedFile
{
    /// <summary>The form field name.</summary>
    public string Name { get; }

    /// <summary>The client-supplied filename. This is untrusted metadata and must not be used as a storage path.</summary>
    public string FileName { get; }

    /// <summary>The client-supplied content type, or application/octet-stream. It does not verify the file's contents.</summary>
    public string ContentType { get; }

    /// <summary>The actual file content length in bytes.</summary>
    public long Length { get; }

    private readonly long _offset;

    private readonly RequestBodyStorage _storage;

    internal UploadedFile(string name, string fileName, string contentType, RequestBodyStorage storage, long offset, long length)
    {
        Name        = name;
        FileName    = fileName;
        ContentType = contentType;
        Length      = length;
        _storage    = storage;
        _offset     = offset;
    }

    /// <summary>Opens an independent, read-only stream. Dispose it after use and finish reading before the request ends.</summary>
    public Stream OpenReadStream() => _storage.OpenRead(_offset, Length);

    /// <summary>Copies the file to an application-owned destination without closing that destination.</summary>
    public async ValueTask CopyToAsync(Stream destination, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        await using var source = OpenReadStream();
        await source.CopyToAsync(destination, ct);
    }
}
