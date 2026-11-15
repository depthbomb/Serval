#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using System.Globalization;
using System.Security.Cryptography;

namespace Serval.Authentication.Passwords;

/// <summary>Produces versioned PBKDF2-SHA256 hashes and verifies them with bounded work.</summary>
public sealed class Pbkdf2PasswordHasher : IPasswordHasher
{
    private readonly int _hashBytes;
    private readonly int _iterations;
    private readonly int _maximumIterations;
    private readonly int _maximumPasswordLength;
    private readonly int _saltBytes;

    /// <summary>Validates and snapshots the hasher's work and size limits.</summary>
    /// <param name="options">The creation targets and verification bounds.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    ///     An iteration, salt, hash, or password-size setting is outside its supported bounds.
    /// </exception>
    public Pbkdf2PasswordHasher(Pbkdf2PasswordHasherOptions options)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(options.Iterations, 10000);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaximumVerificationIterations, options.Iterations);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.MaximumVerificationIterations, 10000000);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.SaltBytes, 16);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.SaltBytes, 64);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.HashBytes, 32);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.HashBytes, 64);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaximumPasswordLength);

        _iterations            = options.Iterations;
        _maximumIterations     = options.MaximumVerificationIterations;
        _saltBytes             = options.SaltBytes;
        _hashBytes             = options.HashBytes;
        _maximumPasswordLength = options.MaximumPasswordLength;
    }

    /// <inheritdoc />
    /// <remarks>Uses a fresh random salt and the serval$pbkdf2-sha256$v1 format.</remarks>
    /// <exception cref="ArgumentNullException">The password is null.</exception>
    /// <exception cref="ArgumentException">The password is empty or exceeds MaximumPasswordLength.</exception>
    public string Hash(string password)
    {
        ArgumentNullException.ThrowIfNull(password);

        if (password.Length == 0 || password.Length > _maximumPasswordLength)
        {
            throw new ArgumentException("The password length is outside the configured bounds.", nameof(password));
        }

        var salt = RandomNumberGenerator.GetBytes(_saltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, _iterations, HashAlgorithmName.SHA256, _hashBytes);
        try
        {
            return string.Create(CultureInfo.InvariantCulture, $"serval$pbkdf2-sha256$v1${_iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(hash);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Malformed input and hashes exceeding the configured verification limits return Failed. A matching hash needs
    ///     rehashing when its iterations, salt size, or derived-byte count is below the configured target.
    /// </remarks>
    public PasswordVerificationResult Verify(string password, string encodedHash)
    {
        if (
            string.IsNullOrEmpty(password)           ||
            password.Length > _maximumPasswordLength ||
            string.IsNullOrEmpty(encodedHash)        ||
            encodedHash.Length > 256)
        {
            return PasswordVerificationResult.Failed;
        }

        // Bound the stored work factor before deriving anything; database content isn't a CPU budget.
        var parts = encodedHash.Split('$');
        if (
            parts.Length != 6                                                                            ||
            parts[0]     != "serval"                                                                     ||
            parts[1]     != "pbkdf2-sha256"                                                              ||
            parts[2]     != "v1"                                                                         ||
            !int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var iterations) ||
            iterations < 10000                                                                           ||
            iterations > _maximumIterations)
        {
            return PasswordVerificationResult.Failed;
        }

        Span<byte> salt     = stackalloc byte[64];
        Span<byte> expected = stackalloc byte[64];
        if (
            !Convert.TryFromBase64String(parts[4], salt, out var saltLength)     ||
            saltLength < 16                                                      ||
            !Convert.TryFromBase64String(parts[5], expected, out var hashLength) ||
            hashLength < 32)
        {
            return PasswordVerificationResult.Failed;
        }

        Span<byte> actual = stackalloc byte[hashLength];
        Rfc2898DeriveBytes.Pbkdf2(password, salt[..saltLength], actual, iterations, HashAlgorithmName.SHA256);

        var valid = CryptographicOperations.FixedTimeEquals(expected[..hashLength], actual);

        CryptographicOperations.ZeroMemory(actual);
        CryptographicOperations.ZeroMemory(expected);

        if (!valid)
        {
            return PasswordVerificationResult.Failed;
        }

        return iterations < _iterations || saltLength < _saltBytes || hashLength < _hashBytes
            ? PasswordVerificationResult.SuccessRehashNeeded
            : PasswordVerificationResult.Success;
    }
}
