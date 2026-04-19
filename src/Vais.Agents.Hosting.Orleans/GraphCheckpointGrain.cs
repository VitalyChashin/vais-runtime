// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Hosting.Orleans;

/// <summary>
/// Default <see cref="IGraphCheckpointGrain"/> implementation. Persists one graph-run's
/// checkpoint JSON under the same storage name as <see cref="AiAgentGrain"/>
/// (<see cref="AiAgentGrain.StorageName"/>) so hosts only have to register one grain
/// storage for the whole stack.
/// </summary>
/// <remarks>
/// Pure state container. Serialization round-trip between <see cref="GraphCheckpoint"/>
/// and the JSON blob happens in <see cref="OrleansCheckpointer"/> — the grain just
/// owns the durable string.
/// </remarks>
public sealed class GraphCheckpointGrain : Grain, IGraphCheckpointGrain
{
    private readonly IPersistentState<GraphCheckpointGrainState> _state;

    /// <summary>Grain constructor. Dependencies resolved from silo DI.</summary>
    public GraphCheckpointGrain(
        [PersistentState("graph-checkpoint", AiAgentGrain.StorageName)] IPersistentState<GraphCheckpointGrainState> state)
    {
        ArgumentNullException.ThrowIfNull(state);
        _state = state;
    }

    /// <inheritdoc />
    public Task<GraphCheckpointSurrogate?> GetAsync()
    {
        if (_state.State.HasCheckpoint)
        {
            return Task.FromResult<GraphCheckpointSurrogate?>(_state.State.Checkpoint);
        }
        return Task.FromResult<GraphCheckpointSurrogate?>(null);
    }

    /// <inheritdoc />
    public async Task SaveAsync(GraphCheckpointSurrogate checkpoint)
    {
        _state.State.HasCheckpoint = true;
        _state.State.Checkpoint = checkpoint;
        await _state.WriteStateAsync();
    }

    /// <inheritdoc />
    public async Task ClearAsync()
    {
        await _state.ClearStateAsync();
        _state.State.HasCheckpoint = false;
        _state.State.Checkpoint = default;
        DeactivateOnIdle();
    }
}
