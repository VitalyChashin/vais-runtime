// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Control;

/// <summary>Lifecycle status of an <see cref="ApprovalRequest"/>.</summary>
public enum ApprovalStatus
{
    /// <summary>Awaiting an operator decision; the mutation is held.</summary>
    Pending = 0,

    /// <summary>An operator approved the request; a matching apply may proceed.</summary>
    Approved = 1,

    /// <summary>An operator rejected the request; the mutation stays blocked.</summary>
    Rejected = 2,
}

/// <summary>
/// A human-in-the-loop approval for a high-risk control-plane mutation (Plan B Phase 3).
/// Bound to a specific manifest by <see cref="ManifestHash"/> so an approval authorizes
/// only the exact artifact that was reviewed — a tampered manifest re-hashes and stays
/// blocked.
/// </summary>
/// <param name="RequestId">Opaque approval id; the agent re-applies after an operator approves it.</param>
/// <param name="Kind">Manifest kind being mutated (e.g. <c>ContainerPlugin</c>).</param>
/// <param name="Name">Resource id/name being mutated.</param>
/// <param name="ManifestHash">Stable hash of the canonical manifest the approval is bound to.</param>
/// <param name="RequestedBy">Principal id that triggered the request.</param>
/// <param name="RequestedAt">When the request was created (UTC).</param>
/// <param name="Status">Current status.</param>
/// <param name="DecidedBy">Operator principal id that approved/rejected; null while pending.</param>
/// <param name="DecidedAt">When the decision was made; null while pending.</param>
public sealed record ApprovalRequest(
    string RequestId,
    string Kind,
    string Name,
    string ManifestHash,
    string RequestedBy,
    DateTimeOffset RequestedAt,
    ApprovalStatus Status,
    string? DecidedBy,
    DateTimeOffset? DecidedAt);

/// <summary>
/// Durable store of <see cref="ApprovalRequest"/>s. Behind an abstraction so a single
/// host can use an in-memory store and a cluster an Orleans grain-backed one (P1).
/// </summary>
public interface IApprovalStore
{
    /// <summary>Return an <see cref="ApprovalStatus.Approved"/> request matching (kind, name, hash), or null.</summary>
    ValueTask<ApprovalRequest?> FindApprovedAsync(string kind, string name, string manifestHash, CancellationToken ct = default);

    /// <summary>
    /// Create (or return the existing) <see cref="ApprovalStatus.Pending"/> request for
    /// (kind, name, hash). Idempotent — re-applying the same held manifest returns the same id.
    /// </summary>
    ValueTask<ApprovalRequest> CreatePendingAsync(string kind, string name, string manifestHash, string requestedBy, CancellationToken ct = default);

    /// <summary>Fetch a request by id, or null when unknown.</summary>
    ValueTask<ApprovalRequest?> GetAsync(string requestId, CancellationToken ct = default);

    /// <summary>List requests, optionally filtered by <paramref name="status"/>.</summary>
    ValueTask<IReadOnlyList<ApprovalRequest>> ListAsync(ApprovalStatus? status = null, CancellationToken ct = default);

    /// <summary>
    /// Approve or reject a pending request. Returns the updated request, or null when the id
    /// is unknown or already decided.
    /// </summary>
    ValueTask<ApprovalRequest?> DecideAsync(string requestId, bool approve, string decidedBy, CancellationToken ct = default);
}

/// <summary>
/// Gate that holds a high-risk mutation until an operator approves it. Wired only when a
/// deployment opts into approvals; absent = no approval enforcement (mutations proceed).
/// </summary>
public interface IApprovalGate
{
    /// <summary>
    /// No-op for non-high-risk kinds, or when an approval matching the canonical manifest
    /// already exists. Otherwise records a pending request and throws
    /// <see cref="ApprovalRequiredException"/> carrying its id — the caller must not mutate.
    /// </summary>
    ValueTask EnsureApprovedAsync(string kind, string name, string manifestCanonical, string requestedBy, CancellationToken ct = default);
}

/// <summary>
/// Thrown by <see cref="IApprovalGate"/> when a high-risk mutation needs operator approval.
/// Maps to <c>202 Accepted</c> at the REST boundary with the request id in the problem extensions.
/// </summary>
public sealed class ApprovalRequiredException : Exception
{
    /// <summary>The pending approval id the operator must approve.</summary>
    public string RequestId { get; }

    /// <summary>Manifest kind being mutated.</summary>
    public string Kind { get; }

    /// <summary>Resource id/name being mutated.</summary>
    public string Name { get; }

    /// <summary>Construct the exception for a held mutation.</summary>
    public ApprovalRequiredException(string kind, string name, string requestId)
        : base($"Mutation of high-risk {kind} '{name}' requires operator approval. Approval request id: {requestId}.")
    {
        Kind = kind;
        Name = name;
        RequestId = requestId;
    }
}
