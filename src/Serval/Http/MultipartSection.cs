#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

namespace Serval.Http;

/// <summary>A multipart section whose body is available only during its streaming callback.</summary>
public sealed class MultipartSection
{
    /// <summary>The submitted field name.</summary>
    public string Name { get; }

    /// <summary>The client filename, or null for a text field. Never use it directly as a storage path.</summary>
    public string? FileName { get; }

    /// <summary>The client content type, with a default of application/octet-stream for files and text/plain for fields.</summary>
    public string ContentType { get; }

    /// <summary>The validated, frozen part headers.</summary>
    public Headers Headers { get; }

    /// <summary>An asynchronous, forward-only body stream. Leave it open and finish reads before the callback returns.</summary>
    public Stream Body { get; }

    internal MultipartSection(string name, string? fileName, string contentType, Headers headers, Stream body)
    {
        Name        = name;
        FileName    = fileName;
        ContentType = contentType;
        Headers     = headers;
        Body        = body;
    }
}
