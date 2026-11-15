#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

namespace Serval.Http.Constants;

/// <summary>Common content coding tokens for Content-Encoding and Accept-Encoding.</summary>
/// <remarks>These are header values. Setting a header does not compress or decompress the body.</remarks>
public static class ContentEncodings
{
    /// <summary>Brotli compression.</summary>
    public const string Brotli = "br";

    /// <summary>Deflate compression with the zlib wrapper required by HTTP.</summary>
    public const string Deflate = "deflate";

    /// <summary>Gzip compression.</summary>
    public const string Gzip = "gzip";

    /// <summary>No content coding; used in Accept-Encoding negotiation.</summary>
    public const string Identity = "identity";

    /// <summary>Zstandard compression.</summary>
    public const string Zstd = "zstd";
}
