// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents;

/// <summary>What kind of recipe a proposal is. Drives how it's surfaced + (later) where it lands when approved.</summary>
public enum RecipeProposalKind
{
    /// <summary>An ordered or co-occurrence sequence of concepts (typically tool calls). The most common Layer-2 induction output.</summary>
    WorkflowRecipe = 0,

    /// <summary>A capability / risk tag suggestion for a single concept (e.g. <c>risk:Destructive</c> on a tool).</summary>
    TagSuggestion = 1,

    /// <summary>A description rewrite for a single concept (Writer.com / DRAFT pattern: better text from observed behavior).</summary>
    DescriptionRewrite = 2,

    /// <summary>
    /// A predictive failure-prior annotation: statistical evidence that a specific
    /// <c>(concept, attribution-path)</c> pair fails at an elevated rate. Induced from the
    /// failure-signal corpus (run-health store + MCP gateway event store) by
    /// <c>FailurePatternInducer</c>. On approval, writes to
    /// <c>FailureOntologyOverlay.Attributions[path].FailurePriors</c>.
    /// </summary>
    FailurePrior = 3,
}

/// <summary>Operator review state for a recipe proposal.</summary>
public enum RecipeProposalStatus
{
    /// <summary>Awaiting review. The default state for any freshly-induced proposal.</summary>
    Pending = 0,

    /// <summary>Operator approved; eligible to flow back into the overlay (Plan D Phase 5).</summary>
    Approved = 1,

    /// <summary>Operator rejected. Stays in the store with the reviewer + reason for audit.</summary>
    Rejected = 2,

    /// <summary>A later proposal superseded this one (same concept, higher support / different shape).</summary>
    Superseded = 3,
}

/// <summary>
/// Risk classification on a proposal. Drives the approval gate — high-risk proposals must
/// go through Plan B's platform-level approval (analogous to mutating <c>ContainerPlugin</c>
/// kinds); low / medium can be approved by any caller with the configured scope.
/// </summary>
public enum RecipeProposalRiskLevel
{
    /// <summary>Description rewrites for non-destructive concepts. Default; safe to auto-apply with a permissive policy.</summary>
    Low = 0,

    /// <summary>Tag suggestions on non-destructive concepts; workflow recipes among already-known tools.</summary>
    Medium = 1,

    /// <summary>Proposing destructive / role-mapping tags, recipes that include destructive concepts, anything that affects authorization.</summary>
    High = 2,
}

/// <summary>
/// One candidate ontology fragment surfaced by an <see cref="IRecipeInducer"/>. Plan D's
/// review surface (<c>vais recipes propose|list|show</c> + the
/// <c>vais approvals approve|reject</c> reuse from Plan B) flows the proposal through
/// human-in-the-loop validation; approved proposals are written into the deployment-local
/// overlay (Plan D Phase 5).
/// </summary>
/// <remarks>
/// "Induction proposes; humans dispose" (research §"Practical recommendations" #2). Every
/// proposal carries support / confidence so weakly-supported ones can be filtered or
/// triaged. The §14.5 invariant (ontology is not a sequencer) means proposals are
/// advisory data — promoting one writes overlay text / tags, never auto-executes anything.
/// </remarks>
public sealed record RecipeProposal
{
    /// <summary>Stable id for this proposal (UUIDv7 / opaque).</summary>
    public required string ProposalId { get; init; }

    /// <summary>Discriminator — workflow recipe vs tag suggestion vs description rewrite.</summary>
    public required RecipeProposalKind Kind { get; init; }

    /// <summary>Target concept name (kind name for north, tool name for south).</summary>
    public required string Concept { get; init; }

    /// <summary>The proposal text — description, comma-list of tags, or human-readable sequence pattern, depending on <see cref="Kind"/>.</summary>
    public required string Body { get; init; }

    /// <summary>Number of supporting trajectory events / runs. Higher is stronger evidence.</summary>
    public required int Support { get; init; }

    /// <summary>Confidence score in [0.0, 1.0] — typically support / total_runs in the queried window.</summary>
    public required double Confidence { get; init; }

    /// <summary>Event ids of the trajectories that contributed to this proposal. Lets reviewers click through to evidence.</summary>
    public required IReadOnlyList<string> SourceTraceIds { get; init; }

    /// <summary>Risk classification driving the approval gate. See <see cref="RecipeProposalRiskLevel"/>.</summary>
    public required RecipeProposalRiskLevel RiskLevel { get; init; }

    /// <summary>Operator review state. New proposals start <see cref="RecipeProposalStatus.Pending"/>.</summary>
    public required RecipeProposalStatus Status { get; init; }

    /// <summary>Wall-clock time the proposal was induced.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>Wall-clock time of operator review. Null until reviewed.</summary>
    public DateTimeOffset? ReviewedAt { get; init; }

    /// <summary>Operator id who reviewed. Null until reviewed.</summary>
    public string? ReviewerId { get; init; }

    /// <summary>Optional human-readable name for the proposal (typically filled by the LLM-assisted inducer; null otherwise).</summary>
    public string? Name { get; init; }
}

/// <summary>
/// Mine a trajectory corpus into candidate <see cref="RecipeProposal"/> records.
/// Implementations are typically stateless — they read events from
/// <see cref="IInterceptorTeeStore"/> per call and emit fresh proposals. Persistence,
/// approval, and overlay write-back are separate concerns (Plan D Phase 4-5).
/// </summary>
public interface IRecipeInducer
{
    /// <summary>Induce candidate proposals from events matching <paramref name="query"/>.</summary>
    Task<IReadOnlyList<RecipeProposal>> InduceAsync(TrajectoryQuery query, CancellationToken cancellationToken = default);
}
