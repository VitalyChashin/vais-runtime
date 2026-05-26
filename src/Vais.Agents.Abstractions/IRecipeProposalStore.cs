// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents;

/// <summary>
/// Filter for <see cref="IRecipeProposalStore.ListAsync"/>. All fields are optional; the store
/// returns proposals that satisfy every non-null filter (AND semantics).
/// </summary>
/// <param name="Concept">Restrict to one anchor concept (the proposal's target tool / kind name).</param>
/// <param name="Kind">Restrict to one proposal kind (workflow recipe, tag suggestion, etc.).</param>
/// <param name="Status">Restrict to one lifecycle status (typically <c>Pending</c> for triage queries).</param>
/// <param name="RiskLevel">Restrict to one risk level (used by approval-gate UI to surface High first).</param>
/// <param name="Limit">Maximum number of proposals to return. Stores default-cap if null.</param>
public sealed record RecipeProposalQuery(
    string? Concept = null,
    RecipeProposalKind? Kind = null,
    RecipeProposalStatus? Status = null,
    RecipeProposalRiskLevel? RiskLevel = null,
    int? Limit = null);

/// <summary>
/// Durable store of induced <see cref="RecipeProposal"/>s. Persists proposals emitted by an
/// <see cref="IRecipeInducer"/>, supports listing for human triage, and gates status
/// transitions (Pending → Approved/Rejected/Superseded). Plan D ships an in-memory default
/// (<c>InMemoryRecipeProposalStore</c>) and a Postgres-backed default
/// (<c>PostgresRecipeProposalStore</c>).
/// </summary>
/// <remarks>
/// <para>
/// Status transitions are constrained: only <see cref="RecipeProposalStatus.Pending"/>
/// proposals can be decided; deciding an already-decided proposal is a no-op that returns
/// the existing record unchanged. This prevents accidental re-promotion.
/// </para>
/// <para>
/// High-risk proposals (<see cref="RecipeProposalRiskLevel.High"/>) require an
/// <c>IApprovalStore</c> approval before they can be flipped to
/// <see cref="RecipeProposalStatus.Approved"/>; without one, the store throws
/// <c>ApprovalRequiredException</c> (both types live in <c>Vais.Agents.Control.Abstractions</c>).
/// Low/Medium risk proposals flip directly. Rejection is always permitted (no approval gate on
/// Reject).
/// </para>
/// </remarks>
public interface IRecipeProposalStore
{
    /// <summary>
    /// Upsert a proposal. Idempotent on <see cref="RecipeProposal.ProposalId"/>. The existing
    /// <see cref="RecipeProposal.Status"/>, <see cref="RecipeProposal.ReviewedAt"/>, and
    /// <see cref="RecipeProposal.ReviewerId"/> are preserved across upserts so that re-running
    /// an inducer cannot reset a human decision.
    /// </summary>
    ValueTask UpsertAsync(RecipeProposal proposal, CancellationToken cancellationToken = default);

    /// <summary>Fetch a proposal by id, or <c>null</c> when unknown.</summary>
    ValueTask<RecipeProposal?> GetAsync(string proposalId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stream proposals matching <paramref name="query"/> in <see cref="RecipeProposal.CreatedAt"/>-
    /// descending order (newest first).
    /// </summary>
    IAsyncEnumerable<RecipeProposal> ListAsync(RecipeProposalQuery query, CancellationToken cancellationToken = default);

    /// <summary>
    /// Approve or reject a pending proposal. For high-risk proposals, this may throw
    /// <c>ApprovalRequiredException</c> on first call — the operator must then approve the
    /// corresponding request via the existing <c>vais approvals approve</c> surface and re-invoke
    /// this method. Returns the updated proposal, or <c>null</c> when the id is unknown. If the
    /// proposal is already decided, returns the existing record unchanged.
    /// </summary>
    ValueTask<RecipeProposal?> DecideAsync(string proposalId, bool approve, string decidedBy, CancellationToken cancellationToken = default);
}
