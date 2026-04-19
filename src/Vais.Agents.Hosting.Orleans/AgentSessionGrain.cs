// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Hosting.Orleans;

/// <summary>
/// Default <see cref="IAgentSessionGrain"/> implementation. Persists the session's
/// history to an <see cref="IPersistentState{TState}"/> store under the same
/// storage name as <see cref="AiAgentGrain"/> (<see cref="AiAgentGrain.StorageName"/>)
/// so hosts only have to register one grain storage.
/// </summary>
/// <remarks>
/// Pure state container in v0.4. The LLM turn-loop does not run here — it runs
/// client-side through <see cref="Core.StatefulAiAgent"/> bound to a client-side
/// <see cref="OrleansAgentSession"/> proxy. This matches the Bedrock AgentCore /
/// OpenAI Assistants split where "session" is the state primitive and execution
/// is a separate concern (future control-plane pillar).
/// </remarks>
public sealed class AgentSessionGrain : Grain, IAgentSessionGrain
{
    private readonly IPersistentState<AgentSessionGrainState> _state;

    /// <summary>Grain constructor. Dependencies resolved from silo DI.</summary>
    public AgentSessionGrain(
        [PersistentState("session", AiAgentGrain.StorageName)] IPersistentState<AgentSessionGrainState> state)
    {
        ArgumentNullException.ThrowIfNull(state);
        _state = state;
    }

    /// <inheritdoc />
    public async Task AppendAsync(ChatTurn turn)
    {
        ArgumentNullException.ThrowIfNull(turn);
        _state.State.History.Add(turn);
        await _state.WriteStateAsync();
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ChatTurn>> GetHistoryAsync()
        => Task.FromResult<IReadOnlyList<ChatTurn>>(_state.State.History.ToArray());

    /// <inheritdoc />
    public async Task ResetAsync()
    {
        _state.State.History = new List<ChatTurn>();
        await _state.WriteStateAsync();
    }

    /// <inheritdoc />
    public async Task DeleteAsync()
    {
        await _state.ClearStateAsync();
        DeactivateOnIdle();
    }
}
