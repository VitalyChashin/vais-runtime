// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Hosting.Orleans;

/// <summary>Persisted state for <see cref="AgentGraphRunGrain"/>.</summary>
[GenerateSerializer]
public sealed class AgentGraphRunGrainState
{
    /// <summary>Whether a run is currently in flight under this id.</summary>
    [Id(0)] public bool Active { get; set; }

    /// <summary>Graph manifest id the run belongs to.</summary>
    [Id(1)] public string GraphId { get; set; } = string.Empty;

    /// <summary>Graph manifest version.</summary>
    [Id(2)] public string Version { get; set; } = string.Empty;

    /// <summary>UTC start time of the current run.</summary>
    [Id(3)] public DateTimeOffset StartedAt { get; set; }

    /// <summary>Whether cancellation has been requested for the current run.</summary>
    [Id(4)] public bool CancelRequested { get; set; }
}

/// <summary>Cluster-wide snapshot returned by <see cref="IAgentGraphRunGrain.GetAsync"/>.</summary>
[GenerateSerializer]
public sealed record GraphRunGrainSnapshot(
    [property: Id(0)] string GraphId,
    [property: Id(1)] string Version,
    [property: Id(2)] DateTimeOffset StartedAt,
    [property: Id(3)] bool CancelRequested);

/// <summary>
/// Per-run coordination grain (key = run id) backing the Orleans
/// <see cref="Vais.Agents.Control.IGraphRunCoordinator"/>. Single-activation cluster-wide, so
/// conflict detection, the cancel flag, and status are reachable from any silo (P1) — while the
/// orchestrator that actually executes the run stays on its originating silo (P6). Holds run
/// *status* only; graph data lives in the checkpointer.
/// </summary>
public interface IAgentGraphRunGrain : IGrainWithStringKey
{
    /// <summary>Register a fresh run. False if one is already active under this id (conflict).</summary>
    Task<bool> TryStartAsync(string graphId, string version);

    /// <summary>Re-register a resuming run (unconditional; clears any prior cancel flag).</summary>
    Task MarkActiveAsync(string graphId, string version);

    /// <summary>Request cancellation. Durable; observed by the executing silo's poll.</summary>
    Task RequestCancelAsync();

    /// <summary>Whether cancellation has been requested.</summary>
    Task<bool> IsCancelRequestedAsync();

    /// <summary>Mark the run terminal and release the slot.</summary>
    Task CompleteAsync();

    /// <summary>Snapshot if a run is active, else <see langword="null"/>.</summary>
    Task<GraphRunGrainSnapshot?> GetAsync();
}

/// <summary>Default <see cref="IAgentGraphRunGrain"/> — one activation per run id.</summary>
public sealed class AgentGraphRunGrain : Grain, IAgentGraphRunGrain
{
    private readonly IPersistentState<AgentGraphRunGrainState> _state;

    /// <summary>Grain constructor; state facet resolved from silo DI.</summary>
    public AgentGraphRunGrain(
        [PersistentState("graph-run", AiAgentGrain.StorageName)] IPersistentState<AgentGraphRunGrainState> state)
    {
        ArgumentNullException.ThrowIfNull(state);
        _state = state;
    }

    /// <inheritdoc />
    public async Task<bool> TryStartAsync(string graphId, string version)
    {
        if (_state.State.Active)
        {
            return false;
        }
        _state.State = new AgentGraphRunGrainState
        {
            Active = true,
            GraphId = graphId,
            Version = version,
            StartedAt = DateTimeOffset.UtcNow,
            CancelRequested = false,
        };
        await _state.WriteStateAsync();
        return true;
    }

    /// <inheritdoc />
    public async Task MarkActiveAsync(string graphId, string version)
    {
        _state.State = new AgentGraphRunGrainState
        {
            Active = true,
            GraphId = graphId,
            Version = version,
            StartedAt = DateTimeOffset.UtcNow,
            CancelRequested = false,
        };
        await _state.WriteStateAsync();
    }

    /// <inheritdoc />
    public async Task RequestCancelAsync()
    {
        if (!_state.State.CancelRequested)
        {
            _state.State.CancelRequested = true;
            await _state.WriteStateAsync();
        }
    }

    /// <inheritdoc />
    public Task<bool> IsCancelRequestedAsync() => Task.FromResult(_state.State.CancelRequested);

    /// <inheritdoc />
    public async Task CompleteAsync()
    {
        _state.State.Active = false;
        await _state.ClearStateAsync();
        DeactivateOnIdle();
    }

    /// <inheritdoc />
    public Task<GraphRunGrainSnapshot?> GetAsync()
        => Task.FromResult(_state.State.Active
            ? new GraphRunGrainSnapshot(_state.State.GraphId, _state.State.Version, _state.State.StartedAt, _state.State.CancelRequested)
            : null);
}
