// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents;

/// <summary>Distinguishes mechanical (runtime) failures from quality (eval) failures.</summary>
public enum FailureAxis
{
    /// <summary>A runtime-observable failure: tool error, LLM retry, provider fallback, turn abort.</summary>
    Mechanical = 0,
    /// <summary>A quality failure surfaced by the eval harness: judge miss, assertion fail.</summary>
    Quality = 1,
}

/// <summary>
/// A named failure concept in the shared vocabulary. Mechanical concepts map 1:1 to
/// <see cref="RunHealthSignalKind"/> members; sub-concepts extend the base via overlay.
/// </summary>
/// <param name="Name">Unique identifier, e.g. <c>McpToolError</c> or <c>McpToolError/AuthExpired</c>.</param>
/// <param name="Axis">Mechanical (runtime) vs Quality (eval).</param>
/// <param name="DefaultLevel">Baseline severity when no overlay overrides it.</param>
/// <param name="Description">Human-readable description for diagnostic surfaces.</param>
/// <param name="SourceKinds">The <see cref="RunHealthSignalKind"/> values that map to this concept. Empty for quality concepts and overlay sub-concepts.</param>
/// <param name="ParentName">Parent concept name for sub-concept hierarchy. Null for top-level concepts.</param>
/// <param name="Tags">Optional diagnostic tags (e.g. <c>transient</c>, <c>auth</c>, <c>quota</c>).</param>
public sealed record FailureConcept(
    string Name,
    FailureAxis Axis,
    FailureLevel DefaultLevel,
    string Description,
    IReadOnlyList<RunHealthSignalKind> SourceKinds,
    string? ParentName = null,
    IReadOnlyList<string>? Tags = null);

/// <summary>
/// A deployment-local rule that overrides default severity for a concept based on whether
/// the failure was recovered or exhausted (turn-fatal).
/// </summary>
/// <param name="ConceptName">The concept this rule applies to.</param>
/// <param name="RecoveredLevel">Level to use when the failure was recovered.</param>
/// <param name="ExhaustedLevel">Level to use when the failure was turn-fatal.</param>
public sealed record FailureSeverityRule(
    string ConceptName,
    FailureLevel RecoveredLevel,
    FailureLevel ExhaustedLevel);

/// <summary>
/// Deployment-local overlay that extends the auto-derived base failure taxonomy with
/// sub-concepts, description overrides, and severity-rule refinements.
/// Content is deployment-specific and never committed to <c>agentic/</c>.
/// </summary>
/// <param name="Concepts">Additional or override concepts merged over the base.</param>
/// <param name="SeverityRules">Per-concept severity overrides.</param>
public sealed record FailureOntologyOverlay(
    IReadOnlyList<FailureConcept>? Concepts = null,
    IReadOnlyList<FailureSeverityRule>? SeverityRules = null)
{
    /// <summary>An empty overlay that leaves the base taxonomy unchanged.</summary>
    public static readonly FailureOntologyOverlay Empty = new();
}
