// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents;

/// <summary>
/// Quality gate invoked before an approved <see cref="RecipeProposalKind.FailurePrior"/> is
/// written to the failure-ontology overlay. Implementations run an <c>EvalSuite</c> against
/// the prior's target agent and check whether mechanical failures are observed (corroboration
/// gate — research §11.7-Q3 "updating ≠ improving").
/// </summary>
public interface IFailurePriorEvalGate
{
    /// <summary>
    /// Evaluate the prior. Returns <c>(true, null)</c> on pass, <c>(false, reason)</c> on reject.
    /// Implementations must fail-open on infra errors (eval suite unavailable, timeout, etc.) so
    /// operator approvals are never silently blocked by eval infrastructure outages.
    /// </summary>
    Task<(bool Passed, string? Reason)> EvaluateAsync(RecipeProposal prior, CancellationToken ct);
}

/// <summary>Always-pass no-op gate. Used when <c>VAIS_FAILURE_PRIOR_EVAL_GATE</c> is not set.</summary>
public sealed class NoOpFailurePriorEvalGate : IFailurePriorEvalGate
{
    /// <summary>Singleton instance.</summary>
    public static readonly NoOpFailurePriorEvalGate Instance = new();

    /// <inheritdoc/>
    public Task<(bool Passed, string? Reason)> EvaluateAsync(RecipeProposal prior, CancellationToken ct)
        => Task.FromResult<(bool, string?)>((true, null));
}
