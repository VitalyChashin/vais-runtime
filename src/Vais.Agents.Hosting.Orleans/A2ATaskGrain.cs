// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Hosting.Orleans;

/// <summary>
/// Default <see cref="IA2ATaskGrain"/> implementation. Persists one A2A task's JSON
/// representation under the same storage name as <see cref="AiAgentGrain"/>
/// (<see cref="AiAgentGrain.StorageName"/>) so hosts only have to register one grain
/// storage.
/// </summary>
/// <remarks>
/// Pure state container. Serialization round-trip between <c>A2A.AgentTask</c> and the
/// JSON blob happens in <see cref="OrleansTaskStore"/> — the grain itself just owns
/// the durable string.
/// </remarks>
public sealed class A2ATaskGrain : Grain, IA2ATaskGrain
{
    private readonly IPersistentState<A2ATaskGrainState> _state;

    /// <summary>Grain constructor. Dependencies resolved from silo DI.</summary>
    public A2ATaskGrain(
        [PersistentState("a2a-task", AiAgentGrain.StorageName)] IPersistentState<A2ATaskGrainState> state)
    {
        ArgumentNullException.ThrowIfNull(state);
        _state = state;
    }

    /// <inheritdoc />
    public Task<A2ATaskSurrogate?> GetAsync()
    {
        if (_state.State.HasTask)
        {
            return Task.FromResult<A2ATaskSurrogate?>(_state.State.Task);
        }
        return Task.FromResult<A2ATaskSurrogate?>(null);
    }

    /// <inheritdoc />
    public async Task SaveAsync(A2ATaskSurrogate task)
    {
        _state.State.HasTask = true;
        _state.State.Task = task;
        await _state.WriteStateAsync();
    }

    /// <inheritdoc />
    public async Task ClearAsync()
    {
        await _state.ClearStateAsync();
        _state.State.HasTask = false;
        _state.State.Task = default;
        DeactivateOnIdle();
    }
}
