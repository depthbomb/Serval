#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

namespace Serval.Http;

/// <summary>Limits for parsed form and JSON POST data. The total request body limit also applies.</summary>
public sealed class FormOptions
{
    /// <summary>The maximum number of submitted field values, including repeated fields. Defaults to 1,024.</summary>
    public int MaxFields { get; set; } = 1024;

    /// <summary>The maximum number of uploaded files. Defaults to 32; zero disables files.</summary>
    public int MaxFiles { get; set; } = 32;

    /// <summary>The maximum decoded field name length in characters. Defaults to 2,048.</summary>
    public int MaxKeyLength { get; set; } = 2048;

    /// <summary>The maximum decoded text field length in characters. Defaults to 1 MiB.</summary>
    public int MaxValueLength { get; set; } = 1024 * 1024;

    /// <summary>The maximum length of an individual file in bytes. Defaults to 128 MiB.</summary>
    public long MaxFileBytes { get; set; } = 128 * 1024 * 1024;

    /// <summary>The maximum body bytes buffered in memory before using a temporary file. Defaults to 64 KiB.</summary>
    public int MemoryThreshold { get; set; } = 64 * 1024;

    /// <summary>The directory for temporary body files, deleted when the request ends.</summary>
    public string TempDirectory { get; set; } = Path.GetTempPath();

    /// <summary>The maximum number of headers in a multipart part. Defaults to 16.</summary>
    public int MaxMultipartHeaders { get; set; } = 16;

    /// <summary>The maximum header bytes in a multipart part. Defaults to 16 KiB.</summary>
    public int MaxMultipartHeaderBytes { get; set; } = 16 * 1024;

    internal FormOptions Copy() => (FormOptions)MemberwiseClone();

    internal bool Matches(FormOptions other)
        => MaxFields                  == other.MaxFields      && MaxFiles            == other.MaxFiles     && MaxKeyLength    == other.MaxKeyLength
           && MaxValueLength          == other.MaxValueLength && MaxFileBytes        == other.MaxFileBytes && MemoryThreshold == other.MemoryThreshold
           && TempDirectory           == other.TempDirectory  && MaxMultipartHeaders == other.MaxMultipartHeaders
           && MaxMultipartHeaderBytes == other.MaxMultipartHeaderBytes;

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxFields);
        ArgumentOutOfRangeException.ThrowIfNegative(MaxFiles);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxKeyLength);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxValueLength);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxFileBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(MemoryThreshold);
        ArgumentException.ThrowIfNullOrWhiteSpace(TempDirectory);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxMultipartHeaders);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxMultipartHeaderBytes);
    }
}
