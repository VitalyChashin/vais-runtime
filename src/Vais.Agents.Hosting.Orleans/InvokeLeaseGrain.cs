// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Hosting.Orleans;

/// <summary>Persisted state for <see cref="InvokeLeaseGrain"/>.</summary>
[GenerateSerializer]
public sealed class InvokeLeaseGrainState
{
    /// <summary>Whether the lease is currently open.</summary>
    [Id(0)] public bool Active { get; set; }

    /// <summary>Run id the leased invoke belongs to (diagnostics).</summary>
    [Id(1)] public string RunId { get; set; } = string.Empty;

    /// <summary>Agent id the leased invoke belongs to (diagnostics).</summary>
    [Id(2)] public string AgentId { get; set; } = string.Empty;

    /// <summary>Soft deadline, extended by each heartbeat (capped at <see cref="HardDeadline"/>).</summary>
    [Id(3)] public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>Absolute ceiling fixed at <see cref="IInvokeLeaseGrain.StartAsync"/>; heartbeats never exceed it.</summary>
    [Id(4)] public DateTimeOffset HardDeadline { get; set; }
}

/// <summary>
/// Per-invoke liveness lease (key = leaseId) backing the Orleans
/// <see cref="Vais.Agents.Core.IInvokeLeaseStore"/>. Single-activation cluster-wide so a session-mode
/// call token is honoured from any silo (P1) — the gateway callback a plugin makes may land on a silo
/// other than the one supervising it. Holds liveness only; the token itself is a stateless HMAC.
/// </summary>
public interface IInvokeLeaseGrain : IGrainWithStringKey
{
    /// <summary>Open the lease with a soft heartbeat deadline and a hard session ceiling.</summary>
    Task StartAsync(string runId, string agentId, int sessionTtlSeconds, int heartbeatTtlSeconds);

    /// <summary>True iff the lease is open and unexpired.</summary>
    Task<bool> IsLiveAsync();

    /// <summary>Extend the soft deadline (never beyond the hard ceiling). No-op once released.</summary>
    Task HeartbeatAsync(int heartbeatTtlSeconds);

    /// <summary>Close the lease and release the slot.</summary>
    Task ReleaseAsync();
}

/// <summary>Default <see cref="IInvokeLeaseGrain"/> — one activation per leaseId.</summary>
public sealed class InvokeLeaseGrain : Grain, IInvokeLeaseGrain
{
    private readonly IPersistentState<InvokeLeaseGrainState> _state;

    /// <summary>Grain constructor; state facet resolved from silo DI.</summary>
    public InvokeLeaseGrain(
        [PersistentState("invoke-lease", AiAgentGrain.StorageName)] IPersistentState<InvokeLeaseGrainState> state)
    {
        ArgumentNullException.ThrowIfNull(state);
        _state = state;
    }

    /// <inheritdoc />
    public async Task StartAsync(string runId, string agentId, int sessionTtlSeconds, int heartbeatTtlSeconds)
    {
        var now = DateTimeOffset.UtcNow;
        var hard = now.AddSeconds(sessionTtlSeconds);
        _state.State = new InvokeLeaseGrainState
        {
            Active = true,
            RunId = runId,
            AgentId = agentId,
            ExpiresAt = Min(now.AddSeconds(heartbeatTtlSeconds), hard),
            HardDeadline = hard,
        };
        await _state.WriteStateAsync();
    }

    /// <inheritdoc />
    public Task<bool> IsLiveAsync()
        => Task.FromResult(_state.State.Active && DateTimeOffset.UtcNow < _state.State.ExpiresAt);

    /// <inheritdoc />
    public async Task HeartbeatAsync(int heartbeatTtlSeconds)
    {
        if (!_state.State.Active) return;
        _state.State.ExpiresAt = Min(DateTimeOffset.UtcNow.AddSeconds(heartbeatTtlSeconds), _state.State.HardDeadline);
        await _state.WriteStateAsync();
    }

    /// <inheritdoc />
    public async Task ReleaseAsync()
    {
        _state.State.Active = false;
        await _state.ClearStateAsync();
        DeactivateOnIdle();
    }

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;
}
