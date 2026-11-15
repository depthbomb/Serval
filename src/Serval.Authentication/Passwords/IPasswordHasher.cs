#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

namespace Serval.Authentication.Passwords;

/// <summary>Describes whether a password matches a stored hash and needs an upgrade.</summary>
public enum PasswordVerificationResult
{
    /// <summary>The password or encoded hash is invalid, unsupported, or does not match.</summary>
    Failed,

    /// <summary>The password matches and the stored parameters meet the configured targets.</summary>
    Success,

    /// <summary>The password matches, but the stored hash should be replaced using current parameters.</summary>
    SuccessRehashNeeded,
}

/// <summary>Hashes passwords and verifies encoded hashes.</summary>
public interface IPasswordHasher
{
    /// <summary>Creates a salted, encoded password hash.</summary>
    /// <param name="password">The plaintext password to hash.</param>
    /// <returns>An encoded hash containing the algorithm, work factor, salt, and derived bytes.</returns>
    public string Hash(string password);

    /// <summary>Verifies a password and reports whether the stored hash needs upgrading.</summary>
    /// <param name="password">The plaintext password supplied during sign-in.</param>
    /// <param name="encodedHash">The stored encoded hash.</param>
    /// <returns>Failure, success, or success with a request to rehash.</returns>
    /// <remarks>
    ///     Implementations should reject malformed or excessive work factors before doing expensive derivation.
    /// </remarks>
    public PasswordVerificationResult Verify(string password, string encodedHash);
}

/// <summary>Sets PBKDF2-SHA256 creation targets and verification bounds.</summary>
public sealed class Pbkdf2PasswordHasherOptions
{
    /// <summary>The iteration count for new hashes. Defaults to 600,000 and must be at least 10,000.</summary>
    public int Iterations { get; set; } = 600000;

    /// <summary>
    ///     The largest stored iteration count accepted for verification. Defaults to 2,000,000; must be at least Iterations
    ///     and at most 10,000,000.
    /// </summary>
    public int MaximumVerificationIterations { get; set; } = 2000000;

    /// <summary>The random salt size for new hashes, from 16 through 64 bytes. Defaults to 16.</summary>
    public int SaltBytes { get; set; } = 16;

    /// <summary>The derived hash size, from 32 through 64 bytes. Defaults to 32.</summary>
    public int HashBytes { get; set; } = 32;

    /// <summary>The positive password length limit in UTF-16 characters. Defaults to 1,024.</summary>
    public int MaximumPasswordLength { get; set; } = 1024;
}
