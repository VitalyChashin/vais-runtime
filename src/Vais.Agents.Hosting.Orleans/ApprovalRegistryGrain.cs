// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Vais.Agents.Control;

namespace Vais.Agents.Hosting.Orleans;

/// <summary>Orleans-serializable mirror of <see cref="ApprovalRequest"/> held in grain state.</summary>
[GenerateSerializer]
internal sealed class ApprovalRecord
{
    /// <summary>Opaque approval id.</summary>
    [Id(0)] public string RequestId { get; set; } = string.Empty;
    /// <summary>Manifest kind.</summary>
    [Id(1)] public string Kind { get; set; } = string.Empty;
    /// <summary>Resource id/name.</summary>
    [Id(2)] public string Name { get; set; } = string.Empty;
    /// <summary>Canonical manifest hash the approval binds to.</summary>
    [Id(3)] public string ManifestHash { get; set; } = string.Empty;
    /// <summary>Principal that requested the mutation.</summary>
    [Id(4)] public string RequestedBy { get; set; } = string.Empty;
    /// <summary>When the request was created (UTC).</summary>
    [Id(5)] public DateTimeOffset RequestedAt { get; set; }
    /// <summary><see cref="ApprovalStatus"/> as an int.</summary>
    [Id(6)] public int Status { get; set; }
    /// <summary>Operator that decided; null while pending.</summary>
    [Id(7)] public string? DecidedBy { get; set; }
    /// <summary>When decided; null while pending.</summary>
    [Id(8)] public DateTimeOffset? DecidedAt { get; set; }
}

/// <summary>Persisted state for <see cref="ApprovalRegistryGrain"/>.</summary>
[GenerateSerializer]
internal sealed class ApprovalRegistryGrainState
{
    /// <summary>All approval requests keyed by request id.</summary>
    [Id(0)] public Dictionary<string, ApprovalRecord> Requests { get; set; } = new();
}

/// <summary>
/// Single-activation registry of all <see cref="ApprovalRequest"/>s (key is a constant), backing the
/// Orleans <see cref="IApprovalStore"/>. One grain so the admin surface can list pending approvals and
/// any silo sees the same state (P1) — approvals are low-volume, human-gated, so a single grain is
/// ample. Persisted, so pending approvals survive a silo restart.
/// </summary>
internal interface IApprovalRegistryGrain : IGrainWithStringKey
{
    /// <summary>Return an Approved record matching (kind, name, hash), or null.</summary>
    Task<ApprovalRecord?> FindApprovedAsync(string kind, string name, string manifestHash);

    /// <summary>Create (or return the existing) Pending record for (kind, name, hash).</summary>
    Task<ApprovalRecord> CreatePendingAsync(string kind, string name, string manifestHash, string requestedBy);

    /// <summary>Fetch a record by id, or null.</summary>
    Task<ApprovalRecord?> GetAsync(string requestId);

    /// <summary>List records, optionally filtered by status (<see cref="ApprovalStatus"/> as int; null = all).</summary>
    Task<List<ApprovalRecord>> ListAsync(int? statusFilter);

    /// <summary>Approve/reject a pending record. Returns the updated record, or null if unknown / already decided.</summary>
    Task<ApprovalRecord?> DecideAsync(string requestId, bool approve, string decidedBy);
}

/// <summary>Default <see cref="IApprovalRegistryGrain"/> — one persisted activation holding all requests.</summary>
internal sealed class ApprovalRegistryGrain : Grain, IApprovalRegistryGrain
{
    private readonly IPersistentState<ApprovalRegistryGrainState> _state;

    /// <summary>Grain constructor; state facet resolved from silo DI.</summary>
    public ApprovalRegistryGrain(
        [PersistentState("approvals", AiAgentGrain.StorageName)] IPersistentState<ApprovalRegistryGrainState> state)
    {
        ArgumentNullException.ThrowIfNull(state);
        _state = state;
    }

    /// <inheritdoc />
    public Task<ApprovalRecord?> FindApprovedAsync(string kind, string name, string manifestHash)
    {
        foreach (var r in _state.State.Requests.Values)
        {
            if (r.Status == (int)ApprovalStatus.Approved && Matches(r, kind, name, manifestHash))
                return Task.FromResult<ApprovalRecord?>(r);
        }
        return Task.FromResult<ApprovalRecord?>(null);
    }

    /// <inheritdoc />
    public async Task<ApprovalRecord> CreatePendingAsync(string kind, string name, string manifestHash, string requestedBy)
    {
        foreach (var r in _state.State.Requests.Values)
        {
            if (r.Status == (int)ApprovalStatus.Pending && Matches(r, kind, name, manifestHash))
                return r;
        }
        var created = new ApprovalRecord
        {
            RequestId = Guid.NewGuid().ToString("N"),
            Kind = kind,
            Name = name,
            ManifestHash = manifestHash,
            RequestedBy = requestedBy,
            RequestedAt = DateTimeOffset.UtcNow,
            Status = (int)ApprovalStatus.Pending,
        };
        _state.State.Requests[created.RequestId] = created;
        await _state.WriteStateAsync();
        return created;
    }

    /// <inheritdoc />
    public Task<ApprovalRecord?> GetAsync(string requestId)
        => Task.FromResult(_state.State.Requests.TryGetValue(requestId, out var r) ? r : null);

    /// <inheritdoc />
    public Task<List<ApprovalRecord>> ListAsync(int? statusFilter)
    {
        var items = _state.State.Requests.Values
            .Where(r => statusFilter is null || r.Status == statusFilter)
            .OrderBy(r => r.RequestedAt)
            .ToList();
        return Task.FromResult(items);
    }

    /// <inheritdoc />
    public async Task<ApprovalRecord?> DecideAsync(string requestId, bool approve, string decidedBy)
    {
        if (!_state.State.Requests.TryGetValue(requestId, out var r) || r.Status != (int)ApprovalStatus.Pending)
            return null;
        r.Status = (int)(approve ? ApprovalStatus.Approved : ApprovalStatus.Rejected);
        r.DecidedBy = decidedBy;
        r.DecidedAt = DateTimeOffset.UtcNow;
        await _state.WriteStateAsync();
        return r;
    }

    private static bool Matches(ApprovalRecord r, string kind, string name, string hash)
        => string.Equals(r.Kind, kind, StringComparison.Ordinal)
        && string.Equals(r.Name, name, StringComparison.Ordinal)
        && string.Equals(r.ManifestHash, hash, StringComparison.Ordinal);
}

/// <summary>
/// Orleans grain-backed <see cref="IApprovalStore"/> — durable, cluster-wide approvals (P1). Delegates
/// to the single <see cref="IApprovalRegistryGrain"/>; a clustered host registers this in place of the
/// in-memory store so pending approvals survive a restart and are visible from any silo.
/// </summary>
public sealed class OrleansApprovalStore : IApprovalStore
{
    private const string RegistryKey = "default";
    private readonly IGrainFactory _grainFactory;

    /// <summary>Creates a store over the given grain factory.</summary>
    public OrleansApprovalStore(IGrainFactory grainFactory)
    {
        ArgumentNullException.ThrowIfNull(grainFactory);
        _grainFactory = grainFactory;
    }

    private IApprovalRegistryGrain Grain() => _grainFactory.GetGrain<IApprovalRegistryGrain>(RegistryKey);

    /// <inheritdoc />
    public async ValueTask<ApprovalRequest?> FindApprovedAsync(string kind, string name, string manifestHash, CancellationToken ct = default)
        => ToRequest(await Grain().FindApprovedAsync(kind, name, manifestHash).ConfigureAwait(false));

    /// <inheritdoc />
    public async ValueTask<ApprovalRequest> CreatePendingAsync(string kind, string name, string manifestHash, string requestedBy, CancellationToken ct = default)
        => ToRequest(await Grain().CreatePendingAsync(kind, name, manifestHash, requestedBy).ConfigureAwait(false))!;

    /// <inheritdoc />
    public async ValueTask<ApprovalRequest?> GetAsync(string requestId, CancellationToken ct = default)
        => ToRequest(await Grain().GetAsync(requestId).ConfigureAwait(false));

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<ApprovalRequest>> ListAsync(ApprovalStatus? status = null, CancellationToken ct = default)
    {
        var records = await Grain().ListAsync(status is null ? null : (int)status).ConfigureAwait(false);
        return records.Select(r => ToRequest(r)!).ToList();
    }

    /// <inheritdoc />
    public async ValueTask<ApprovalRequest?> DecideAsync(string requestId, bool approve, string decidedBy, CancellationToken ct = default)
        => ToRequest(await Grain().DecideAsync(requestId, approve, decidedBy).ConfigureAwait(false));

    private static ApprovalRequest? ToRequest(ApprovalRecord? r)
        => r is null ? null : new ApprovalRequest(
            r.RequestId, r.Kind, r.Name, r.ManifestHash, r.RequestedBy, r.RequestedAt,
            (ApprovalStatus)r.Status, r.DecidedBy, r.DecidedAt);
}
