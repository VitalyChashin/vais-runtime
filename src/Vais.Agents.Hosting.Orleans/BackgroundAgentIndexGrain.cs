// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Hosting.Orleans;

/// <summary>
/// Default <see cref="IBackgroundAgentIndexGrain"/> implementation.
/// Durably tracks all background sub-run handles for a parent run id.
/// </summary>
public sealed class BackgroundAgentIndexGrain : Grain, IBackgroundAgentIndexGrain
{
    private readonly IPersistentState<BackgroundAgentIndexGrainState> _state;

    /// <summary>Grain constructor. Dependencies resolved from silo DI.</summary>
    public BackgroundAgentIndexGrain(
        [PersistentState("bg-index", AiAgentGrain.StorageName)] IPersistentState<BackgroundAgentIndexGrainState> state)
    {
        ArgumentNullException.ThrowIfNull(state);
        _state = state;
    }

    /// <inheritdoc />
    public async Task AddHandleAsync(string handle)
    {
        if (!_state.State.Handles.Contains(handle, StringComparer.Ordinal))
        {
            _state.State.Handles.Add(handle);
            await _state.WriteStateAsync();
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> ListHandlesAsync()
        => Task.FromResult<IReadOnlyList<string>>(_state.State.Handles.ToArray());
}
