// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;

namespace Vais.Agents;

/// <summary>
/// Predicate guarding an <see cref="GraphEdge"/>. Closed hierarchy — the declarative
/// vocabulary ships <see cref="PropertyMatcher"/> + <see cref="AllOf"/> + <see cref="AnyOf"/>
/// + <see cref="Not"/> + <see cref="Always"/>; the <see cref="HandlerRef"/> escape hatch
/// covers anything richer via a DI-resolved <see cref="IGraphEdgePredicate"/>.
/// </summary>
/// <remarks>
/// Kubernetes-style matchers. Same idiom as <c>matchExpressions</c> on a PodSelector —
/// familiar surface for operators and YAML-authored graphs. No expression DSL.
/// </remarks>
public abstract record GraphEdgePredicate
{
    private GraphEdgePredicate() { }

    /// <summary>Always-true predicate. Equivalent to a null <see cref="GraphEdge.When"/>.</summary>
    public sealed record Always : GraphEdgePredicate;

    /// <summary>
    /// Matches when <see cref="Property"/> in graph state satisfies the
    /// <see cref="GraphPredicateOperator"/> against <see cref="Value"/>.
    /// </summary>
    /// <param name="Property">Dotted property path. Top-level keys reference graph state; the well-known <c>lastMessage.text</c> / <c>lastMessage.role</c> paths read the most-recently-appended message from the <c>messages</c> state key.</param>
    /// <param name="Operator">Comparison operator.</param>
    /// <param name="Value">Right-hand value. Ignored for <see cref="GraphPredicateOperator.Exists"/> / <see cref="GraphPredicateOperator.NotExists"/>.</param>
    public sealed record PropertyMatcher(
        string Property,
        GraphPredicateOperator Operator,
        JsonElement? Value = null) : GraphEdgePredicate;

    /// <summary>True when all <see cref="Predicates"/> match.</summary>
    public sealed record AllOf(IReadOnlyList<GraphEdgePredicate> Predicates) : GraphEdgePredicate;

    /// <summary>True when any of <see cref="Predicates"/> match.</summary>
    public sealed record AnyOf(IReadOnlyList<GraphEdgePredicate> Predicates) : GraphEdgePredicate;

    /// <summary>True when <see cref="Predicate"/> does not match.</summary>
    public sealed record Not(GraphEdgePredicate Predicate) : GraphEdgePredicate;

    /// <summary>Dispatches to a DI-resolved <see cref="IGraphEdgePredicate"/> implementation.</summary>
    public sealed record HandlerRef(GraphHandlerRef Handler) : GraphEdgePredicate;
}

/// <summary>
/// Comparison operator used by <see cref="GraphEdgePredicate.PropertyMatcher"/>.
/// Ten operators matching the Kubernetes <c>matchExpressions</c> vocabulary
/// plus numeric ordering.
/// </summary>
public enum GraphPredicateOperator
{
    /// <summary>Equality. Numeric, string, and boolean comparable.</summary>
    Eq,

    /// <summary>Inverse equality.</summary>
    NotEq,

    /// <summary>Numeric strictly greater than.</summary>
    Gt,

    /// <summary>Numeric greater than or equal.</summary>
    Gte,

    /// <summary>Numeric strictly less than.</summary>
    Lt,

    /// <summary>Numeric less than or equal.</summary>
    Lte,

    /// <summary>String / array contains. Case-sensitive.</summary>
    Contains,

    /// <summary>Inverse contains.</summary>
    NotContains,

    /// <summary>Property exists in state (any value, including null).</summary>
    Exists,

    /// <summary>Property does not exist in state.</summary>
    NotExists,
}

/// <summary>
/// Consumer hook for <see cref="GraphEdgePredicate.HandlerRef"/>. Resolved from DI
/// by the orchestrator at edge-evaluation time; must be registered before graph
/// invocation for the referenced <see cref="GraphHandlerRef.TypeName"/>.
/// </summary>
public interface IGraphEdgePredicate
{
    /// <summary>Evaluate against the current graph state. Called synchronously during edge evaluation.</summary>
    /// <param name="state">Read-only view of graph state at the decision point.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask<bool> EvaluateAsync(
        IReadOnlyDictionary<string, JsonElement> state,
        CancellationToken cancellationToken = default);
}
