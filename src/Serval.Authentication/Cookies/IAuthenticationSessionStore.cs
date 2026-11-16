#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

namespace Serval.Authentication.Cookies;

/// <summary>Stores authentication tickets under scheme-scoped credential digests.</summary>
/// <remarks>
///     Isolate mutable tickets on both reads and writes. Implement renewal atomically so a concurrent revocation cannot be
///     undone. Keys are SHA-256 digests, never the browser's bearer credential.
/// </remarks>
public interface IAuthenticationSessionStore
{
    /// <summary>Creates or replaces a stored session.</summary>
    /// <param name="key">The opaque scheme-scoped storage key.</param>
    /// <param name="ticket">The ticket to store with an expiration time.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>An operation that completes when the ticket is stored.</returns>
    public ValueTask StoreAsync(string key, AuthenticationTicket ticket, CancellationToken cancellationToken = default);

    /// <summary>Looks up a live session without sharing mutable ticket state.</summary>
    /// <param name="key">The opaque scheme-scoped storage key.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>An isolated ticket, or null if the session is absent or expired.</returns>
    public ValueTask<AuthenticationTicket?> FindAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Replaces a session only if the existing entry is still live.</summary>
    /// <param name="key">The opaque scheme-scoped storage key.</param>
    /// <param name="ticket">The replacement ticket with its new expiration.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>True if renewed; false if the session expired or was removed.</returns>
    /// <remarks>The existence check and update must be atomic. Renewal must never recreate a revoked session.</remarks>
    public ValueTask<bool> RenewAsync(string key, AuthenticationTicket ticket, CancellationToken cancellationToken = default);

    /// <summary>Revokes a session if it exists.</summary>
    /// <param name="key">The opaque scheme-scoped storage key.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>An operation that completes when the entry is absent.</returns>
    public ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default);
}

/// <summary>Limits the number of sessions retained by the in-memory store.</summary>
public sealed class MemoryAuthenticationSessionStoreOptions
{
    /// <summary>The positive maximum number of stored sessions. Defaults to 10,000.</summary>
    public int Capacity { get; set; } = 10000;
}

/// <summary>Stores bounded, expiring sessions within one process.</summary>
/// <remarks>
///     Uses locked updates and copies tickets. Sessions disappear when the process exits; replace
///     IAuthenticationSessionStore for persistent or shared storage.
/// </remarks>
public sealed class MemoryAuthenticationSessionStore : IAuthenticationSessionStore
{
    private DateTimeOffset _nextCleanup;

    private readonly int                                      _capacity;
    private readonly Lock                                     _gate     = new();
    private readonly Dictionary<string, AuthenticationTicket> _sessions = new(StringComparer.Ordinal);
    private readonly TimeProvider                             _time;

    /// <summary>Creates an in-memory session store.</summary>
    /// <param name="timeProvider">The clock used for expiration and cleanup.</param>
    /// <param name="options">The positive session capacity.</param>
    /// <exception cref="ArgumentOutOfRangeException">Capacity is not positive.</exception>
    public MemoryAuthenticationSessionStore(TimeProvider timeProvider, MemoryAuthenticationSessionStoreOptions options)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.Capacity);

        _capacity = options.Capacity;
        _time     = timeProvider;
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentException">The ticket has no future expiration.</exception>
    /// <exception cref="InvalidOperationException">The store is full after expired entries are removed.</exception>
    public ValueTask StoreAsync(string key, AuthenticationTicket ticket, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Validate(ticket);

        lock (_gate)
        {
            Cleanup();
            if (_sessions.Count >= _capacity && !_sessions.ContainsKey(key))
            {
                throw new InvalidOperationException("The authentication session store is full.");
            }

            _sessions[key] = ticket.Copy();
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<AuthenticationTicket?> FindAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (_sessions.TryGetValue(key, out var ticket))
            {
                if (ticket.Properties.ExpiresUtc > _time.GetUtcNow())
                {
                    return ValueTask.FromResult<AuthenticationTicket?>(ticket.Copy());
                }

                _sessions.Remove(key);
            }
        }

        return ValueTask.FromResult<AuthenticationTicket?>(null);
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentException">The replacement ticket has no future expiration.</exception>
    public ValueTask<bool> RenewAsync(string key, AuthenticationTicket ticket, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Validate(ticket);

        lock (_gate)
        {
            // Keep lookup and replacement under one lock so renewal cannot bring back a revoked session.
            if (!_sessions.TryGetValue(key, out var current) || current.Properties.ExpiresUtc <= _time.GetUtcNow())
            {
                return ValueTask.FromResult(false);
            }

            _sessions[key] = ticket.Copy();
            return ValueTask.FromResult(true);
        }
    }

    /// <inheritdoc />
    public ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            _sessions.Remove(key);
        }

        return ValueTask.CompletedTask;
    }

    private void Validate(AuthenticationTicket ticket)
    {
        if (ticket.Properties.ExpiresUtc is not {} expires || expires <= _time.GetUtcNow())
        {
            throw new ArgumentException("A future session expiration is required.", nameof(ticket));
        }
    }

    private void Cleanup()
    {
        var now = _time.GetUtcNow();
        if (now < _nextCleanup && _sessions.Count < _capacity)
        {
            return;
        }

        foreach (var key in _sessions.Where(pair => pair.Value.Properties.ExpiresUtc <= now).Select(pair => pair.Key).ToArray())
        {
            _sessions.Remove(key);
        }

        _nextCleanup = now.AddMinutes(1);
    }
}
