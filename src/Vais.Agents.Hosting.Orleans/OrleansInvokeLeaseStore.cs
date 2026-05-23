// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Vais.Agents.Core;

namespace Vais.Agents.Hosting.Orleans;

/// <summary>
/// Orleans grain-backed <see cref="IInvokeLeaseStore"/>. Each invoke lease is a single-activation
/// <see cref="IInvokeLeaseGrain"/> keyed by leaseId, so a session-mode call token's liveness is
/// reachable from any silo (P1) — required because a plugin's gateway callback can land on a silo
/// other than the one supervising it. The call-token sibling of <see cref="OrleansGraphRunCoordinator"/>.
/// </summary>
public sealed class OrleansInvokeLeaseStore : IInvokeLeaseStore
{
    private readonly IGrainFactory _grainFactory;

    /// <summary>Creates a store over the given grain factory.</summary>
    public OrleansInvokeLeaseStore(IGrainFactory grainFactory)
    {
        ArgumentNullException.ThrowIfNull(grainFactory);
        _grainFactory = grainFactory;
    }

    private IInvokeLeaseGrain Grain(string leaseId) => _grainFactory.GetGrain<IInvokeLeaseGrain>(leaseId);

    /// <inheritdoc />
    public async ValueTask StartAsync(string leaseId, string runId, string agentId,
        int sessionTtlSeconds, int heartbeatTtlSeconds, CancellationToken ct = default)
        => await Grain(leaseId).StartAsync(runId, agentId, sessionTtlSeconds, heartbeatTtlSeconds).ConfigureAwait(false);

    /// <inheritdoc />
    public async ValueTask<bool> IsLiveAsync(string leaseId, CancellationToken ct = default)
        => await Grain(leaseId).IsLiveAsync().ConfigureAwait(false);

    /// <inheritdoc />
    public async ValueTask HeartbeatAsync(string leaseId, int heartbeatTtlSeconds, CancellationToken ct = default)
        => await Grain(leaseId).HeartbeatAsync(heartbeatTtlSeconds).ConfigureAwait(false);

    /// <inheritdoc />
    public async ValueTask ReleaseAsync(string leaseId, CancellationToken ct = default)
        => await Grain(leaseId).ReleaseAsync().ConfigureAwait(false);
}
