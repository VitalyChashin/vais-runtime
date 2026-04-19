// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Hosting.Orleans;

/// <summary>
/// Default <see cref="IAgentRunJournalGrain"/> implementation. Persists the run's
/// journal to an <see cref="IPersistentState{TState}"/> store under the same
/// storage name as <see cref="AiAgentGrain"/> (<see cref="AiAgentGrain.StorageName"/>)
/// so hosts only have to register one grain storage.
/// </summary>
/// <remarks>
/// Pure state container. The cache-replay decision lives client-side inside
/// <see cref="Core.DefaultToolCallDispatcher"/>; the grain just durably owns the
/// append-only list of <see cref="JournalEntrySurrogate"/> entries.
/// </remarks>
public sealed class AgentRunJournalGrain : Grain, IAgentRunJournalGrain
{
    private readonly IPersistentState<AgentRunJournalGrainState> _state;

    /// <summary>Grain constructor. Dependencies resolved from silo DI.</summary>
    public AgentRunJournalGrain(
        [PersistentState("run-journal", AiAgentGrain.StorageName)] IPersistentState<AgentRunJournalGrainState> state)
    {
        ArgumentNullException.ThrowIfNull(state);
        _state = state;
    }

    /// <inheritdoc />
    public async Task AppendAsync(JournalEntrySurrogate entry)
    {
        _state.State.Entries.Add(entry);
        await _state.WriteStateAsync();
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<JournalEntrySurrogate>> GetEntriesAsync()
        => Task.FromResult<IReadOnlyList<JournalEntrySurrogate>>(_state.State.Entries.ToArray());

    /// <inheritdoc />
    public async Task ClearAsync()
    {
        await _state.ClearStateAsync();
        _state.State.Entries = new List<JournalEntrySurrogate>();
        DeactivateOnIdle();
    }
}
