#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Microsoft.Extensions.DependencyInjection;
using Serval.Authentication;
using Serval.Authentication.Passwords;
using Serval.Tests.Support;

namespace Serval.Tests.Authentication;

public sealed class PasswordAuthenticationTests
{
    [Fact]
    public void HashesUseRandomSaltAndDetectWrongPasswords()
    {
        var hasher = Hasher();
        var one    = hasher.Hash("correct horse 🔑");
        var two    = hasher.Hash("correct horse 🔑");
        Assert.NotEqual(one, two);
        Assert.Equal(PasswordVerificationResult.Success, hasher.Verify("correct horse 🔑", one));
        Assert.Equal(PasswordVerificationResult.Failed, hasher.Verify("wrong", one));
    }

    [Fact]
    public void StrongerConfigurationRequestsARehashWithoutDowngrading()
    {
        var older = Hasher();
        var newer = Hasher(20000);
        Assert.Equal(PasswordVerificationResult.SuccessRehashNeeded, newer.Verify("password", older.Hash("password")));
        Assert.Equal(PasswordVerificationResult.Success, older.Verify("password", newer.Hash("password")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("serval$pbkdf2-sha256$v1$2147483647$AAAA$AAAA")]
    [InlineData("serval$pbkdf2-sha256$v1$-1$AAAA$AAAA")]
    [InlineData("serval$pbkdf2-sha256$v2$10000$AAAA$AAAA")]
    [InlineData("serval$pbkdf2-sha256$v1$10000$not base64$not base64")]
    public void MalformedAndExcessiveWorkFactorsFail(string hash)
        => Assert.Equal(PasswordVerificationResult.Failed, Hasher().Verify("password", hash));

    [Fact]
    public void PasswordLengthIsBounded()
    {
        var hasher = Hasher();
        Assert.Throws<ArgumentException>(() => hasher.Hash(""));
        Assert.Throws<ArgumentException>(() => hasher.Hash(new string('x', 1025)));
        Assert.Equal(PasswordVerificationResult.Failed, hasher.Verify(new string('x', 1025), hasher.Hash("password")));
    }

    [Fact]
    public async Task AccountStoreSupportsDisabledUsersMissingUsersAndAtomicRehash()
    {
        var store         = new AccountStore { Account = new PasswordAccount("1", Hasher().Hash("password"), SecurityTestApplication.User()) };
        var authenticator = new PasswordAuthenticator(store, Hasher(20000));
        Assert.True((await authenticator.AuthenticateAsync("alice", "password")).Succeeded);
        Assert.Equal(1, store.Updates);
        Assert.False((await authenticator.AuthenticateAsync("alice", "wrong")).Succeeded);
        store.Account = store.Account! with { Enabled = false };
        Assert.False((await authenticator.AuthenticateAsync("alice", "password")).Succeeded);
        store.Account = null;
        Assert.False((await authenticator.AuthenticateAsync("unknown", "password")).Succeeded);
    }

    [Fact]
    public async Task ConcurrentPasswordResetPreventsSignInDuringRehash()
    {
        var store = new AccountStore { Account = new PasswordAccount("1", Hasher().Hash("password"), SecurityTestApplication.User()), AllowUpdate = false };
        Assert.False((await new PasswordAuthenticator(store, Hasher(20000)).AuthenticateAsync("alice", "password")).Succeeded);
    }

    [Fact]
    public async Task AccountRegistrationSupportsScopedStoresAndAReplaceableHasher()
    {
        await using var app = new SecurityTestApplication(builder =>
        {
            builder.Services.AddSingleton<IPasswordHasher>(Hasher());
            builder.AddAuthentication().AddPasswordAccounts<AccountStore>();
        });
        await using var request = app.Request();
        var             store   = (AccountStore)request.Context.Services.GetRequiredService<IPasswordAccountStore>();
        store.Account = new PasswordAccount("1", Hasher().Hash("password"), SecurityTestApplication.User());
        var authenticator = request.Context.Services.GetRequiredService<PasswordAuthenticator>();
        Assert.True((await authenticator.AuthenticateAsync("alice", "password")).Succeeded);
    }

    private static Pbkdf2PasswordHasher Hasher(int iterations = 10000) => new(new Pbkdf2PasswordHasherOptions { Iterations = iterations });

    private sealed class AccountStore : IPasswordAccountStore
    {
        public PasswordAccount? Account     { get; set; }
        public bool             AllowUpdate { get; init; } = true;
        public int              Updates     { get; private set; }

        public ValueTask<PasswordAccount?> FindAsync(string login, CancellationToken cancellationToken = default) => ValueTask.FromResult(Account);

        public ValueTask<bool> ReplaceHashAsync(string accountId, string expectedHash, string newHash, CancellationToken cancellationToken = default)
        {
            if (!AllowUpdate || Account?.PasswordHash != expectedHash)
            {
                return ValueTask.FromResult(false);
            }

            Account = Account with { PasswordHash = newHash };
            Updates++;
            return ValueTask.FromResult(true);
        }
    }
}
