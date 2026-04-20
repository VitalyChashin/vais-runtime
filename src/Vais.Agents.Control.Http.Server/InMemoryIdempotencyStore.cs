// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace Vais.Agents.Control.Http;

/// <summary>
/// Single-node, in-process <see cref="IIdempotencyStore"/>. Backed by a
/// <see cref="ConcurrentDictionary{TKey, TValue}"/>; evicts expired entries via
/// a background <see cref="Timer"/> firing every
/// <see cref="IdempotencyOptions.EvictionInterval"/>. Lossy on process restart —
/// suitable for dev / single-node / non-durable deployments. Durable consumers
/// register <c>OrleansIdempotencyStore</c> from <c>Vais.Agents.Hosting.Orleans</c>
/// instead.
/// </summary>
/// <remarks>
/// <para>
/// <b>Atomicity.</b> <see cref="TryBeginAsync"/> uses <see cref="ConcurrentDictionary{TKey, TValue}.GetOrAdd(TKey, Func{TKey, TValue})"/>
/// with an <c>InFlight</c> factory — the CAS semantics mean the winning caller
/// sees <see cref="IdempotencyBeginStatus.New"/> and every concurrent loser sees
/// the pre-existing entry.
/// </para>
/// <para>
/// <b>Eviction.</b> The background timer scans the dictionary on each tick and
/// removes entries whose <c>ExpiresAt</c> is in the past. Tick cadence is
/// configurable via <see cref="IdempotencyOptions.EvictionInterval"/> (default
/// 5 minutes). On store disposal the timer is stopped.
/// </para>
/// </remarks>
public sealed class InMemoryIdempotencyStore : IIdempotencyStore, IDisposable
{
    private readonly ConcurrentDictionary<IdempotencyKey, Entry> _entries = new();
    private readonly TimeSpan _ttl;
    private readonly Timer? _evictionTimer;
    private readonly TimeProvider _timeProvider;

    /// <summary>Construct using the supplied options and the ambient <see cref="TimeProvider.System"/>.</summary>
    public InMemoryIdempotencyStore(IOptions<IdempotencyOptions> options)
        : this(options, TimeProvider.System)
    {
    }

    /// <summary>Construct with an explicit <paramref name="timeProvider"/>. Test-only overload.</summary>
    public InMemoryIdempotencyStore(IOptions<IdempotencyOptions> options, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        var opts = options.Value;
        _ttl = opts.Ttl;
        _timeProvider = timeProvider;
        if (opts.EvictionInterval > TimeSpan.Zero)
        {
            _evictionTimer = new Timer(EvictExpired, state: null, opts.EvictionInterval, opts.EvictionInterval);
        }
    }

    /// <inheritdoc />
    public ValueTask<IdempotencyBeginResult> TryBeginAsync(
        IdempotencyKey key,
        string fingerprint,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(fingerprint);

        var now = _timeProvider.GetUtcNow();

        // Reserve-or-observe loop. The common case is CAS-win via GetOrAdd; the
        // rare case is a stale-expired or released-expired entry that survived
        // long enough to collide — handle by compare-and-swap replacement.
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var newEntry = new Entry(EntryState.InFlight, fingerprint, CachedResponse: null, ExpiresAt: now + _ttl);
            var stored = _entries.GetOrAdd(key, newEntry);

            if (ReferenceEquals(stored, newEntry))
            {
                // We won the CAS; this caller owns the reservation.
                return ValueTask.FromResult(new IdempotencyBeginResult(IdempotencyBeginStatus.New));
            }

            // Pre-existing entry. Classify.
            if (stored.ExpiresAt <= now)
            {
                // Stale — try to replace atomically so the "expired leftover" doesn't
                // look like a live InFlight or a stale Replay to the caller.
                if (_entries.TryUpdate(key, newEntry, stored))
                {
                    return ValueTask.FromResult(new IdempotencyBeginResult(IdempotencyBeginStatus.New));
                }
                // Someone raced us; re-read and re-classify.
                continue;
            }

            if (stored.State == EntryState.InFlight)
            {
                return ValueTask.FromResult(new IdempotencyBeginResult(IdempotencyBeginStatus.InFlight));
            }

            // Completed entry. Match fingerprint ⇒ replay; mismatch ⇒ reject.
            if (string.Equals(stored.Fingerprint, fingerprint, StringComparison.Ordinal))
            {
                return ValueTask.FromResult(new IdempotencyBeginResult(
                    IdempotencyBeginStatus.Replay,
                    CachedResponse: stored.CachedResponse));
            }

            return ValueTask.FromResult(new IdempotencyBeginResult(
                IdempotencyBeginStatus.Mismatch,
                ExistingFingerprint: stored.Fingerprint));
        }
    }

    /// <inheritdoc />
    public ValueTask CompleteAsync(IdempotencyKey key, CachedResponse response, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);

        if (!_entries.TryGetValue(key, out var existing))
        {
            // Entry evicted mid-handler (TTL set too low or eviction storm) —
            // no-op. The caller's response still flows downstream; the next
            // retry just re-begins from scratch.
            return ValueTask.CompletedTask;
        }

        var updated = existing with
        {
            State = EntryState.Completed,
            CachedResponse = response,
            ExpiresAt = response.CompletedAt + _ttl,
        };
        // Last-write-wins; a concurrent Complete from another worker (which shouldn't
        // happen given TryBeginAsync's atomicity) simply overwrites.
        _entries[key] = updated;
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask ReleaseAsync(IdempotencyKey key, CancellationToken cancellationToken)
    {
        _entries.TryRemove(key, out _);
        return ValueTask.CompletedTask;
    }

    /// <summary>Dispose the background eviction timer.</summary>
    public void Dispose()
    {
        _evictionTimer?.Dispose();
    }

    private void EvictExpired(object? _)
    {
        var now = _timeProvider.GetUtcNow();
        foreach (var kvp in _entries)
        {
            if (kvp.Value.ExpiresAt <= now)
            {
                // Compare-before-remove so we don't evict entries a concurrent
                // writer just refreshed.
                var collection = (System.Collections.Generic.ICollection<KeyValuePair<IdempotencyKey, Entry>>)_entries;
                collection.Remove(kvp);
            }
        }
    }

    private enum EntryState
    {
        InFlight = 0,
        Completed = 1,
    }

    private sealed record Entry(
        EntryState State,
        string Fingerprint,
        CachedResponse? CachedResponse,
        DateTimeOffset ExpiresAt);
}
