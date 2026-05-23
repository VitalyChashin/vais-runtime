// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Collections.Concurrent;

namespace Vais.Agents.Core;

/// <summary>
/// Tracks the liveness of in-flight session-mode plugin invocations behind an abstraction so the
/// registry can be in-process (single host) or grain-backed (cluster-wide, P1). A session-mode call
/// token — one that carries a leaseId — is honoured by the gateway only while its lease is live: the
/// lease is opened at invoke start, extended on each token renewal, and released when the invoke ends,
/// so a leaked token dies with the session instead of living out a wall-clock TTL.
/// </summary>
/// <remarks>
/// Two deadlines bound a lease: a soft deadline extended by <see cref="HeartbeatAsync"/> (which gives
/// crash-safety — if the supervising silo dies, heartbeats stop and the lease lapses within one
/// heartbeat window) and a hard ceiling fixed at <see cref="StartAsync"/> from the session TTL (which
/// caps the absolute lifetime even while renewals keep arriving).
/// </remarks>
public interface IInvokeLeaseStore
{
    /// <summary>
    /// Opens a lease. <paramref name="sessionTtlSeconds"/> sets the absolute ceiling;
    /// <paramref name="heartbeatTtlSeconds"/> sets the initial soft deadline (capped at the ceiling).
    /// </summary>
    ValueTask StartAsync(string leaseId, string runId, string agentId,
        int sessionTtlSeconds, int heartbeatTtlSeconds, CancellationToken ct = default);

    /// <summary>True iff the lease is open and neither its soft deadline nor its hard ceiling has passed.</summary>
    ValueTask<bool> IsLiveAsync(string leaseId, CancellationToken ct = default);

    /// <summary>Extends the soft deadline (never beyond the hard ceiling). No-op if the lease is gone.</summary>
    ValueTask HeartbeatAsync(string leaseId, int heartbeatTtlSeconds, CancellationToken ct = default);

    /// <summary>Closes the lease; subsequent <see cref="IsLiveAsync"/> returns false.</summary>
    ValueTask ReleaseAsync(string leaseId, CancellationToken ct = default);
}

/// <summary>
/// In-memory <see cref="IInvokeLeaseStore"/> — correct for a single silo, where the gateway callback
/// hits the same process that opened the lease (Docker standalone, the primary co-tenant target). NOT
/// cluster-safe: in a multi-silo deployment a callback can land on a silo that never saw the lease, so
/// a clustered host must register the Orleans-backed store instead (documented scaling gap per P1).
/// </summary>
public sealed class InMemoryInvokeLeaseStore : IInvokeLeaseStore
{
    private readonly ConcurrentDictionary<string, Lease> _leases = new();

    private readonly record struct Lease(DateTimeOffset ExpiresAt, DateTimeOffset HardDeadline);

    /// <inheritdoc />
    public ValueTask StartAsync(string leaseId, string runId, string agentId,
        int sessionTtlSeconds, int heartbeatTtlSeconds, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var hard = now.AddSeconds(sessionTtlSeconds);
        _leases[leaseId] = new Lease(Min(now.AddSeconds(heartbeatTtlSeconds), hard), hard);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<bool> IsLiveAsync(string leaseId, CancellationToken ct = default)
    {
        if (_leases.TryGetValue(leaseId, out var l))
        {
            if (DateTimeOffset.UtcNow < l.ExpiresAt) return ValueTask.FromResult(true);
            _leases.TryRemove(leaseId, out _); // prune lapsed lease (covers the crash-without-release case)
        }
        return ValueTask.FromResult(false);
    }

    /// <inheritdoc />
    public ValueTask HeartbeatAsync(string leaseId, int heartbeatTtlSeconds, CancellationToken ct = default)
    {
        if (_leases.TryGetValue(leaseId, out var l))
            _leases[leaseId] = l with { ExpiresAt = Min(DateTimeOffset.UtcNow.AddSeconds(heartbeatTtlSeconds), l.HardDeadline) };
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask ReleaseAsync(string leaseId, CancellationToken ct = default)
    {
        _leases.TryRemove(leaseId, out _);
        return ValueTask.CompletedTask;
    }

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;
}
