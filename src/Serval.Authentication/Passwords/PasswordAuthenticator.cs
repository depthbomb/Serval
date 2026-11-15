#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using System.Security.Claims;
using System.Security.Cryptography;

namespace Serval.Authentication.Passwords;

/// <summary>Contains the stored credential and principal for a password account.</summary>
/// <param name="Id">The stable account identifier used for hash replacement.</param>
/// <param name="PasswordHash">The stored encoded password hash.</param>
/// <param name="Principal">
///     The principal returned after successful verification; it must have an authenticated identity.
/// </param>
/// <param name="Enabled">Whether password sign-in is enabled; defaults to true.</param>
public sealed record PasswordAccount(string Id, string PasswordHash, ClaimsPrincipal Principal, bool Enabled = true)
{
    /// <summary>Returns the type name without exposing account or hash contents.</summary>
    /// <returns>The string PasswordAccount.</returns>
    public override string ToString() => nameof(PasswordAccount);
}

// Applications own lookup normalization, account storage, lockouts, and password policy.
/// <summary>Provides application-owned account lookup and conditional hash replacement.</summary>
/// <remarks>The application owns login normalization, persistence, lockouts, and password policy.</remarks>
public interface IPasswordAccountStore
{
    /// <summary>Looks up the account identified by the supplied login.</summary>
    /// <param name="login">The login identifier; normalization is the store's responsibility.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The account, or null if it is unknown.</returns>
    public ValueTask<PasswordAccount?> FindAsync(string login, CancellationToken cancellationToken = default);

    // Compare the old hash before replacing it so a concurrent password reset is not overwritten.
    /// <summary>Replaces a hash only if the stored value still matches the expected hash.</summary>
    /// <param name="accountId">The stable account identifier.</param>
    /// <param name="expectedHash">The hash read during authentication.</param>
    /// <param name="newHash">The newly generated encoded hash.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>True if replaced; false if the account or hash changed.</returns>
    /// <remarks>Compare and replace atomically so rehashing cannot overwrite a concurrent password reset.</remarks>
    public ValueTask<bool> ReplaceHashAsync(string accountId, string expectedHash, string newHash, CancellationToken cancellationToken = default);
}

/// <summary>Verifies account credentials and upgrades outdated hashes without issuing a session.</summary>
public sealed class PasswordAuthenticator
{
    private readonly string                _dummyHash;
    private readonly IPasswordHasher       _hasher;
    private readonly IPasswordAccountStore _store;

    /// <summary>Creates an authenticator and a dummy hash for unknown-account checks.</summary>
    /// <param name="store">The application's account store.</param>
    /// <param name="hasher">The hasher used for verification and upgrades.</param>
    public PasswordAuthenticator(IPasswordAccountStore store, IPasswordHasher hasher) : this(store, hasher, PasswordDummyHash.Create(hasher)) { }

    internal PasswordAuthenticator(IPasswordAccountStore store, IPasswordHasher hasher, string dummyHash)
    {
        _store     = store;
        _hasher    = hasher;
        _dummyHash = dummyHash;
    }

    /// <summary>Verifies account credentials and conditionally upgrades the stored hash.</summary>
    /// <param name="login">The application's login identifier.</param>
    /// <param name="password">The plaintext password to verify.</param>
    /// <param name="scheme">The scheme recorded in the returned ticket; defaults to Password.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>
    ///     A successful copied ticket, or a failure for invalid, disabled, or concurrently changed credentials.
    /// </returns>
    /// <remarks>
    ///     Unknown accounts still perform a hash verification. This method does not set RequestContext.User or create a
    ///     cookie; pass the successful principal to a sign-in handler.
    /// </remarks>
    /// <exception cref="ArgumentNullException">The login or password is null.</exception>
    /// <exception cref="ArgumentException">The stored principal has no authenticated identity.</exception>
    public async ValueTask<AuthenticationResult> AuthenticateAsync(string            login,
                                                                   string            password,
                                                                   string            scheme            = "Password",
                                                                   CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(login);
        ArgumentNullException.ThrowIfNull(password);

        cancellationToken.ThrowIfCancellationRequested();

        var account      = await _store.FindAsync(login, cancellationToken);
        // Unknown accounts still pay for a real hash check instead of taking a cheap early return.
        var verification = _hasher.Verify(password, account?.PasswordHash ?? _dummyHash);
        if (account is null || !account.Enabled || verification == PasswordVerificationResult.Failed)
        {
            return AuthenticationResult.Fail("Invalid credentials.");
        }

        if (
            verification == PasswordVerificationResult.SuccessRehashNeeded &&
            !await _store.ReplaceHashAsync(account.Id, account.PasswordHash, _hasher.Hash(password), cancellationToken)
        )
        {
            // A concurrent password change invalidates the credential we just verified.
            return AuthenticationResult.Fail("The credentials changed during sign-in.");
        }

        return AuthenticationResult.Success(new AuthenticationTicket(account.Principal, scheme, new AuthenticationProperties()).Copy());
    }
}

internal sealed class PasswordDummyHash
{
    internal string Value { get; }

    public PasswordDummyHash(IPasswordHasher hasher)
    {
        Value = Create(hasher);
    }

    internal static string Create(IPasswordHasher hasher) => hasher.Hash(Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
}
