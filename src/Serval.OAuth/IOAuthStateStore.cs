#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using System.Security.Cryptography;
using System.Text;
using Serval.Authentication;

namespace Serval.OAuth;

/// <summary>Stores the server-side data for one pending browser sign-in attempt.</summary>
public sealed class OAuthState
{
    /// <summary>The provider scheme that owns this attempt.</summary>
    public required string Scheme { get; init; }

    /// <summary>The digest of the browser correlation credential.</summary>
    public required string CorrelationHash { get; init; }

    /// <summary>The secret PKCE verifier retained for code exchange.</summary>
    public required string CodeVerifier { get; init; }

    /// <summary>The callback URI used for this attempt.</summary>
    public required Uri RedirectUri { get; init; }

    /// <summary>The validated local path to return to after sign-in.</summary>
    public required string ReturnUrl { get; init; }

    /// <summary>The absolute expiration of the pending attempt.</summary>
    public required DateTimeOffset ExpiresUtc { get; init; }

    /// <summary>The properties to pass to the local sign-in scheme on success.</summary>
    public AuthenticationProperties Properties { get; init; } = new();

    /// <summary>Copies the state and its mutable properties dictionary.</summary>
    /// <returns>An isolated copy suitable for storage or request processing.</returns>
    public OAuthState Copy() => new()
    {
        Scheme          = Scheme,
        CorrelationHash = CorrelationHash,
        CodeVerifier    = CodeVerifier,
        RedirectUri     = RedirectUri,
        ReturnUrl       = ReturnUrl,
        ExpiresUtc      = ExpiresUtc,
        Properties = Properties with
        {
            Items = new Dictionary<string, string>(Properties.Items, StringComparer.Ordinal)
        },
    };
}

/// <summary>Stores expiring, single-use browser sign-in state.</summary>
/// <remarks>Implementations must isolate mutable properties and make matching, removal, and retrieval atomic.</remarks>
public interface IOAuthStateStore
{
    /// <summary>Stores a new pending sign-in attempt.</summary>
    /// <param name="key">The digest of the authorization state token.</param>
    /// <param name="state">The attempt with a future expiration.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>An operation that completes when the state is stored.</returns>
    public ValueTask StoreAsync(string key, OAuthState state, CancellationToken cancellationToken = default);

    // Atomically remove and return a live state only when both scheme and browser binding match.
    // A mismatch must not consume another browser's pending sign-in.
    /// <summary>Atomically consumes live state only when the provider and browser binding match.</summary>
    /// <param name="key">The digest of the authorization state token.</param>
    /// <param name="scheme">The expected provider scheme.</param>
    /// <param name="correlationHash">The digest of the browser's correlation credential.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>An isolated consumed state, or null if absent, expired, or mismatched.</returns>
    /// <remarks>
    ///     A mismatch must not consume another browser's pending sign-in. A successful lookup removes the entry so
    ///     callbacks cannot replay it.
    /// </remarks>
    public ValueTask<OAuthState?> TakeAsync(string key, string scheme, string correlationHash, CancellationToken cancellationToken = default);
}

/// <summary>Limits pending sign-ins in the single-process state store.</summary>
public sealed class MemoryOAuthStateStoreOptions
{
    /// <summary>The positive maximum number of pending sign-ins. Defaults to 10,000.</summary>
    public int Capacity { get; set; } = 10000;
}

/// <summary>Stores bounded, single-use OAuth state in memory.</summary>
/// <remarks>
///     Entries disappear when the process exits. Replace IOAuthStateStore when multiple processes need to share pending
///     attempts.
/// </remarks>
public sealed class MemoryOAuthStateStore : IOAuthStateStore
{
    private          DateTimeOffset _nextCleanup;
    private readonly int            _capacity;
    private readonly Lock           _gate = new();

    private readonly Dictionary<string, OAuthState> _states = new(StringComparer.Ordinal);
    private readonly TimeProvider                   _time;

    /// <summary>Creates an in-memory OAuth state store.</summary>
    /// <param name="timeProvider">The clock used for expiration and cleanup.</param>
    /// <param name="options">The positive pending-attempt capacity.</param>
    /// <exception cref="ArgumentOutOfRangeException">Capacity is not positive.</exception>
    public MemoryOAuthStateStore(TimeProvider timeProvider, MemoryOAuthStateStoreOptions options)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.Capacity);

        _time     = timeProvider;
        _capacity = options.Capacity;
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentException">The expiration is not in the future or the key already exists.</exception>
    /// <exception cref="InvalidOperationException">The store is full after expired entries are removed.</exception>
    public ValueTask StoreAsync(string key, OAuthState state, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var now = _time.GetUtcNow();
        if (state.ExpiresUtc <= now)
        {
            throw new ArgumentException("A future state expiration is required.", nameof(state));
        }

        lock (_gate)
        {
            if (now >= _nextCleanup || _states.Count >= _capacity)
            {
                foreach (var expired in _states.Where(pair => pair.Value.ExpiresUtc <= now).Select(pair => pair.Key).ToArray())
                {
                    _states.Remove(expired);
                }

                _nextCleanup = now.AddMinutes(1);
            }

            if (_states.Count >= _capacity)
            {
                throw new InvalidOperationException("The OAuth state store is full.");
            }

            _states.Add(key, state.Copy());
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<OAuthState?> TakeAsync(string            key,
                                            string            scheme,
                                            string            correlationHash,
                                            CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (!_states.TryGetValue(key, out var state))
            {
                return ValueTask.FromResult<OAuthState?>(null);
            }

            if (state.ExpiresUtc <= _time.GetUtcNow())
            {
                _states.Remove(key);
                return ValueTask.FromResult<OAuthState?>(null);
            }

            if (
                state.Scheme != scheme ||
                !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(state.CorrelationHash), Encoding.UTF8.GetBytes(correlationHash)))
            {
                // A guessed state token must not let another browser cancel the real sign-in attempt.
                return ValueTask.FromResult<OAuthState?>(null);
            }

            _states.Remove(key);
            return ValueTask.FromResult<OAuthState?>(state.Copy());
        }
    }
}
