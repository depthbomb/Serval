#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using System.Security.Claims;
using Serval.Authentication.Passwords;

namespace Serval.Sample.CookieAuth;

// Public demo credentials and in-memory storage keep this recipe self-contained.
// A deployed app needs durable accounts, login throttling, and account recovery.
public sealed class DemoAccountStore : IPasswordAccountStore
{
    private PasswordAccount _account;

    private readonly Lock _gate = new();

    public DemoAccountStore(IPasswordHasher hasher)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, "demo-1"),
            new Claim(ClaimTypes.Name, "demo"),
        ], "Password"));
        _account = new PasswordAccount("demo-1", hasher.Hash("serval-sample-password"), principal);
    }

    public ValueTask<PasswordAccount?> FindAsync(string login, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        lock (_gate)
        {
            return ValueTask.FromResult(login.Equals("demo", StringComparison.OrdinalIgnoreCase) ? _account : null);
        }
    }

    public ValueTask<bool> ReplaceHashAsync(string            accountId,
                                            string            expectedHash,
                                            string            newHash,
                                            CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (_account.Id != accountId || _account.PasswordHash != expectedHash)
            {
                return ValueTask.FromResult(false);
            }

            _account = _account with
            {
                PasswordHash = newHash,
            };
            return ValueTask.FromResult(true);
        }
    }
}
