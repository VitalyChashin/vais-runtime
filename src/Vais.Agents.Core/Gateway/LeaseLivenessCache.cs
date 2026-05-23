// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Collections.Concurrent;

namespace Vais.Agents.Core;

/// <summary>
/// Per-process short-TTL cache over <see cref="IInvokeLeaseStore.IsLiveAsync"/> so the gateway hot path
/// (one liveness check per LLM/tool call) does not make a store/grain round trip on every request. A
/// revoked, released, or expired lease becomes invisible within at most the cache window. This is the
/// affordable realisation of the "validate the lease on every call" posture.
/// </summary>
public sealed class LeaseLivenessCache
{
    private readonly IInvokeLeaseStore _store;
    private readonly TimeSpan _cacheTtl;
    private readonly ConcurrentDictionary<string, Entry> _cache = new();

    private readonly record struct Entry(bool Live, long CheckedAtTicks);

    /// <summary>Default cache window is 5 seconds.</summary>
    public LeaseLivenessCache(IInvokeLeaseStore store, TimeSpan? cacheTtl = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        _cacheTtl = cacheTtl ?? TimeSpan.FromSeconds(5);
    }

    /// <summary>Returns lease liveness, serving a recent cached result when available.</summary>
    public async ValueTask<bool> IsLiveAsync(string leaseId, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        if (_cache.TryGetValue(leaseId, out var e)
            && now - new DateTimeOffset(e.CheckedAtTicks, TimeSpan.Zero) < _cacheTtl)
        {
            return e.Live;
        }

        var live = await _store.IsLiveAsync(leaseId, ct).ConfigureAwait(false);
        _cache[leaseId] = new Entry(live, now.UtcTicks);
        return live;
    }
}
