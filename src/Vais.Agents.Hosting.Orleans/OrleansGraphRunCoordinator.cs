// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Vais.Agents.Control;

namespace Vais.Agents.Hosting.Orleans;

/// <summary>
/// Orleans grain-backed <see cref="IGraphRunCoordinator"/>. Each run is a single-activation
/// <see cref="IAgentGraphRunGrain"/> keyed by run id, so conflict detection, cancellation, and
/// status are reachable from any silo (P1). The graph-scoped sibling of
/// <see cref="OrleansBackgroundAgentTracker"/>.
/// </summary>
public sealed class OrleansGraphRunCoordinator : IGraphRunCoordinator
{
    private readonly IGrainFactory _grainFactory;

    /// <summary>Creates a coordinator over the given grain factory.</summary>
    public OrleansGraphRunCoordinator(IGrainFactory grainFactory)
    {
        ArgumentNullException.ThrowIfNull(grainFactory);
        _grainFactory = grainFactory;
    }

    private IAgentGraphRunGrain Grain(string runId) => _grainFactory.GetGrain<IAgentGraphRunGrain>(runId);

    /// <inheritdoc />
    public async ValueTask<bool> TryStartAsync(string runId, string graphId, string version, CancellationToken ct = default)
        => await Grain(runId).TryStartAsync(graphId, version).ConfigureAwait(false);

    /// <inheritdoc />
    public async ValueTask MarkActiveAsync(string runId, string graphId, string version, CancellationToken ct = default)
        => await Grain(runId).MarkActiveAsync(graphId, version).ConfigureAwait(false);

    /// <inheritdoc />
    public async ValueTask RequestCancelAsync(string runId, CancellationToken ct = default)
        => await Grain(runId).RequestCancelAsync().ConfigureAwait(false);

    /// <inheritdoc />
    public async ValueTask<bool> IsCancelRequestedAsync(string runId, CancellationToken ct = default)
        => await Grain(runId).IsCancelRequestedAsync().ConfigureAwait(false);

    /// <inheritdoc />
    public async ValueTask CompleteAsync(string runId, GraphRunOutcome outcome, CancellationToken ct = default)
        => await Grain(runId).CompleteAsync().ConfigureAwait(false);

    /// <inheritdoc />
    public async ValueTask<GraphRunSnapshot?> GetAsync(string runId, CancellationToken ct = default)
    {
        var s = await Grain(runId).GetAsync().ConfigureAwait(false);
        return s is null ? null : new GraphRunSnapshot(runId, s.GraphId, s.Version, s.StartedAt, s.CancelRequested);
    }
}
