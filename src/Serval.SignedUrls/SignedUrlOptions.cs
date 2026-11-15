#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

namespace Serval.SignedUrls;

/// <summary>Configures URL signing before the service is registered.</summary>
public sealed class SignedUrlOptions
{
    /// <summary>A secret, cryptographically random key of at least 32 bytes, shared by all application instances.</summary>
    /// <remarks>The service copies this key. Persist it to keep issued URLs valid across restarts.</remarks>
    public byte[] SigningKey { get; set; } = [];

    /// <summary>The reserved, case-insensitive query parameter carrying the token. Defaults to signature.</summary>
    public string SignatureParameterName { get; set; } = "signature";

    /// <summary>The maximum escaped path and query length in characters, including the token. Defaults to 32768.</summary>
    /// <remarks>The scheme and authority of an absolute URL do not count toward this limit.</remarks>
    public int MaxUrlLength { get; set; } = 32768;

    internal void Validate()
    {
        if (SigningKey is not { Length: >= 32 })
        {
            throw new ArgumentException("A signing key of at least 32 random bytes is required.", nameof(SigningKey));
        }

        if (string.IsNullOrEmpty(SignatureParameterName) || SignatureParameterName.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '_' and not '-'))
        {
            throw new ArgumentException("The signature parameter name must contain only ASCII letters, digits, underscores, or hyphens.", nameof(SignatureParameterName));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(MaxUrlLength, 256);
    }
}
