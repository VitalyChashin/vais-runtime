// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Vais.Agents.Control.Policy.Opa;

/// <summary>
/// TTL-bounded cache of <see cref="PolicyDecision"/>s keyed by SHA-256
/// hex of the canonical-JSON input payload. Cheap + concurrent + bounded.
/// </summary>
/// <remarks>
/// <para>
/// Overflow behaviour: when <see cref="Count"/> exceeds the bound, the
/// oldest 25% of entries (by write timestamp) are purged. Not strict
/// LRU — access time isn't tracked — but good enough for the typical
/// case where active policies touch a working set far smaller than
/// the bound, and degrades gracefully under pathological input
/// diversity.
/// </para>
/// <para>
/// Concurrency: a two-evaluator race on the same key will cause
/// duplicate OPA calls; the second writer overwrites the cache slot.
/// Acceptable — OPA evaluation is idempotent, duplicates are rare under
/// the 5s default TTL, and the alternative (per-key async lock) adds
/// contention that isn't worth the savings.
/// </para>
/// </remarks>
internal sealed class DecisionCache
{
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _ttl;
    private readonly int _maxEntries;

    public DecisionCache(TimeProvider timeProvider, TimeSpan ttl, int maxEntries)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        if (maxEntries <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxEntries), maxEntries, "must be > 0");
        }
        _timeProvider = timeProvider;
        _ttl = ttl;
        _maxEntries = maxEntries;
    }

    /// <summary>Number of entries currently cached.</summary>
    public int Count => _entries.Count;

    /// <summary>
    /// Returns <c>true</c> when a fresh decision exists for
    /// <paramref name="key"/>; expired or missing entries return
    /// <c>false</c>.
    /// </summary>
    public bool TryGet(string key, out PolicyDecision decision)
    {
        decision = default;
        if (_ttl == TimeSpan.Zero)
        {
            return false;
        }
        if (!_entries.TryGetValue(key, out var entry))
        {
            return false;
        }
        if (_timeProvider.GetUtcNow() - entry.WrittenAt >= _ttl)
        {
            _entries.TryRemove(key, out _);
            return false;
        }
        decision = entry.Decision;
        return true;
    }

    /// <summary>
    /// Store <paramref name="decision"/> under <paramref name="key"/>.
    /// Triggers an overflow purge when the entry count exceeds the
    /// configured bound.
    /// </summary>
    public void Set(string key, PolicyDecision decision)
    {
        if (_ttl == TimeSpan.Zero)
        {
            return;
        }
        _entries[key] = new Entry(decision, _timeProvider.GetUtcNow());
        if (_entries.Count > _maxEntries)
        {
            Purge();
        }
    }

    private void Purge()
    {
        // Shed oldest 25% by timestamp. Snapshot the key/timestamp pairs,
        // sort, and remove. Concurrent writes during the purge race
        // cleanly — the snapshot is consistent, removes are safe, and
        // any key written after the snapshot stays.
        var snapshot = _entries.ToArray();
        var removeCount = Math.Max(1, snapshot.Length / 4);
        var toRemove = snapshot
            .OrderBy(kv => kv.Value.WrittenAt)
            .Take(removeCount);
        foreach (var kv in toRemove)
        {
            _entries.TryRemove(kv.Key, out _);
        }
    }

    /// <summary>Compute the SHA-256 hex digest of <paramref name="inputJson"/>; the cache key for an input payload.</summary>
    public static string ComputeKey(string inputJson)
    {
        ArgumentNullException.ThrowIfNull(inputJson);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(inputJson));
        return Convert.ToHexStringLower(bytes);
    }

    private readonly record struct Entry(PolicyDecision Decision, DateTimeOffset WrittenAt);
}
