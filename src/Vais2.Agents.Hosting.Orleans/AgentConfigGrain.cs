// Copyright (c) 2026 VAIS2 Platform contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais2.Agents.Hosting.Orleans;

/// <summary>
/// Default <see cref="IAgentConfigGrain"/> implementation. Persists shared per-agent
/// config (currently just <see cref="IAgentConfigGrain.GetSystemPromptAsync"/>).
/// </summary>
public sealed class AgentConfigGrain : Grain, IAgentConfigGrain
{
    private readonly IPersistentState<AgentConfigGrainState> _state;

    /// <summary>Grain constructor. Dependencies resolved from silo DI.</summary>
    public AgentConfigGrain(
        [PersistentState("config", AiAgentGrain.StorageName)] IPersistentState<AgentConfigGrainState> state)
    {
        ArgumentNullException.ThrowIfNull(state);
        _state = state;
    }

    /// <inheritdoc />
    public Task<string?> GetSystemPromptAsync()
        => Task.FromResult(_state.State.SystemPrompt);

    /// <inheritdoc />
    public async Task SetSystemPromptAsync(string? value)
    {
        _state.State.SystemPrompt = value;
        await _state.WriteStateAsync();
    }

    /// <inheritdoc />
    public async Task DeleteAsync()
    {
        await _state.ClearStateAsync();
        DeactivateOnIdle();
    }
}
