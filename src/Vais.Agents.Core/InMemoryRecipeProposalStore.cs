// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace Vais.Agents.Core;

/// <summary>
/// In-memory <see cref="IRecipeProposalStore"/> default. Suitable for single-host
/// deployments, tests, and bootstrap. A clustered host pairs the persisting
/// <c>PostgresRecipeProposalStore</c> with the same approval-check delegate; the two impls
/// are observationally interchangeable.
/// </summary>
/// <remarks>
/// <para>
/// Status transitions are enforced atomically per proposal id (compare-and-swap on the
/// <see cref="ConcurrentDictionary{TKey, TValue}"/>). Already-decided proposals return
/// unchanged from <see cref="DecideAsync"/> — Plan D §"Induction proposes; humans dispose"
/// means decisions are sticky.
/// </para>
/// <para>
/// The optional <c>highRiskApprovalCheck</c> delegate keeps this project Control-free.
/// CompositionRoot wires the real implementation: a call into <c>IApprovalStore</c> that
/// throws <c>ApprovalRequiredException</c> when no matching approval exists. Tests supply a
/// stub. When the delegate is <c>null</c>, no gate runs — appropriate for trusted
/// single-operator deployments and the in-memory bootstrap path.
/// </para>
/// </remarks>
public sealed class InMemoryRecipeProposalStore : IRecipeProposalStore
{
    private readonly ConcurrentDictionary<string, RecipeProposal> _byId = new(StringComparer.Ordinal);
    private readonly Func<RecipeProposal, string, CancellationToken, ValueTask>? _highRiskApprovalCheck;

    /// <summary>Construct an empty store with no high-risk gate.</summary>
    public InMemoryRecipeProposalStore() { }

    /// <summary>
    /// Construct with an optional high-risk gate. When the gate is non-null, it is invoked
    /// before flipping a <see cref="RecipeProposalRiskLevel.High"/> proposal to
    /// <see cref="RecipeProposalStatus.Approved"/>; the gate must throw if approval is missing.
    /// </summary>
    public InMemoryRecipeProposalStore(Func<RecipeProposal, string, CancellationToken, ValueTask>? highRiskApprovalCheck)
    {
        _highRiskApprovalCheck = highRiskApprovalCheck;
    }

    /// <inheritdoc />
    public ValueTask UpsertAsync(RecipeProposal proposal, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        _byId.AddOrUpdate(
            proposal.ProposalId,
            proposal,
            (_, existing) => proposal with
            {
                // Preserve human-decision fields across re-induction; the inducer never owns them.
                Status = existing.Status == RecipeProposalStatus.Pending ? proposal.Status : existing.Status,
                ReviewedAt = existing.ReviewedAt,
                ReviewerId = existing.ReviewerId,
            });
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<RecipeProposal?> GetAsync(string proposalId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proposalId);
        return ValueTask.FromResult(_byId.TryGetValue(proposalId, out var p) ? p : null);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<RecipeProposal> ListAsync(
        RecipeProposalQuery query,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var snapshot = _byId.Values.ToArray();
        var filtered = snapshot
            .Where(p => query.Concept is null || string.Equals(p.Concept, query.Concept, StringComparison.Ordinal))
            .Where(p => query.Kind is null || p.Kind == query.Kind)
            .Where(p => query.Status is null || p.Status == query.Status)
            .Where(p => query.RiskLevel is null || p.RiskLevel == query.RiskLevel)
            .OrderByDescending(p => p.CreatedAt)
            .Take(query.Limit ?? int.MaxValue);
        foreach (var p in filtered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return p;
            await Task.Yield();
        }
    }

    /// <inheritdoc />
    public async ValueTask<RecipeProposal?> DecideAsync(string proposalId, bool approve, string decidedBy, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proposalId);
        ArgumentNullException.ThrowIfNull(decidedBy);

        if (!_byId.TryGetValue(proposalId, out var current)) return null;

        // Decisions are sticky.
        if (current.Status != RecipeProposalStatus.Pending) return current;

        // High-risk approvals must satisfy the optional approval-check gate before flipping.
        // Reject is always permitted (no gate on Reject).
        if (approve && current.RiskLevel == RecipeProposalRiskLevel.High && _highRiskApprovalCheck is not null)
        {
            await _highRiskApprovalCheck(current, decidedBy, cancellationToken).ConfigureAwait(false);
        }

        var updated = current with
        {
            Status = approve ? RecipeProposalStatus.Approved : RecipeProposalStatus.Rejected,
            ReviewedAt = DateTimeOffset.UtcNow,
            ReviewerId = decidedBy,
        };

        // Compare-and-swap: if another thread decided concurrently, return their decision.
        if (_byId.TryUpdate(proposalId, updated, current)) return updated;
        return _byId.TryGetValue(proposalId, out var afterRace) ? afterRace : null;
    }
}
