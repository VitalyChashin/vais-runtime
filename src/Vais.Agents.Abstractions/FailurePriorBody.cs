// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents;

/// <summary>
/// Typed body of a <see cref="RecipeProposalKind.FailurePrior"/> proposal. Serialised as JSON
/// into <see cref="RecipeProposal.Body"/> by <c>FailurePatternInducer</c>; deserialised by
/// the overlay writer when approved. Intentionally free of <c>SuccessCount</c>: the
/// failure-signal corpus (run-health store + MCP gateway event store) records only failures,
/// so a true fail-rate denominator is unavailable without a separate trajectory-store query.
/// v1 uses <see cref="FailureCount"/> as the MinSupport gate; the operator sees it as the key metric.
/// <see cref="RecipeProposal.Confidence"/> is set to <c>0.0</c> to signal "not computed" rather than
/// the misleading <c>1.0</c>.
/// </summary>
public sealed record FailurePriorBody
{
    /// <summary>
    /// First <c>/</c>-delimited segment of <see cref="AttributionPath"/>.
    /// <list type="bullet">
    ///   <item>Single-segment path (agent-level signal) → the agent id itself; <see cref="ToolName"/> is null.</item>
    ///   <item>Two-segment path for <c>ToolError</c> → the agent id; for <c>McpToolError</c> (synthesised
    ///   by <c>FailureSearchService</c>) → the MCP server id (no agent context available in the gateway store).</item>
    ///   <item>Three-segment path → the agent id.</item>
    /// </list>
    /// </summary>
    public required string AgentName { get; init; }

    /// <summary>Failure concept name from the ontology catalog (e.g. <c>McpToolError</c>, <c>ToolError</c>).</summary>
    public required string ConceptName { get; init; }

    /// <summary>
    /// Deployment-grounded attribution path that keys the overlay write target
    /// (<c>FailureOntologyOverlay.Attributions[AttributionPath].FailurePriors</c>).
    /// Stamped by <c>RunHealthSignalSubscriber</c> for bus-sourced signals and synthesised as
    /// <c>{gatewayId}/{toolName}</c> for MCP gateway events.
    /// </summary>
    public required string AttributionPath { get; init; }

    /// <summary>
    /// Tool name — the last <c>/</c>-delimited segment of <see cref="AttributionPath"/> when
    /// the path has two or more segments. Null when <see cref="AttributionPath"/> is a bare
    /// tool name (no agent / gateway prefix).
    /// </summary>
    public string? ToolName { get; init; }

    /// <summary>Number of failure signals in the induction window that matched this (concept, path) group.</summary>
    public required int FailureCount { get; init; }

    /// <summary>Timestamp of the oldest failure in the group (UTC).</summary>
    public required DateTimeOffset FirstSeen { get; init; }

    /// <summary>Timestamp of the most recent failure in the group (UTC).</summary>
    public required DateTimeOffset LastSeen { get; init; }
}
