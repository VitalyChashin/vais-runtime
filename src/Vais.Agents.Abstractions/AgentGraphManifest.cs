// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;

namespace Vais.Agents;

/// <summary>
/// Declarative specification for a graph of agents. Parallel to <see cref="AgentManifest"/>:
/// one manifest = one addressable graph in the registry. Loaded from YAML/JSON under
/// <c>apiVersion: vais.agents/v1</c>, <c>kind: AgentGraph</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Minimal v0.9 surface.</b> Ships the Kubernetes-style matcher language for edge
/// predicates, the {set/increment/append} effect vocabulary, and the closed node-kind
/// set (<c>Agent</c>, <c>Code</c>, <c>Interrupt</c>, <c>End</c>). Customisation beyond
/// the declarative vocabulary rides the <see cref="GraphHandlerRef"/> escape hatch on
/// predicates, effects, and code-backed nodes.
/// </para>
/// </remarks>
/// <param name="Id">Stable identifier. Unique within the registry namespace / tenant scope.</param>
/// <param name="Version">Immutable version tag. Updates create a new version.</param>
/// <param name="Entry">Id of the entry node. Must exist in <paramref name="Nodes"/>.</param>
/// <param name="Nodes">All nodes in the graph. Ids unique within the manifest.</param>
/// <param name="Edges">Edges between nodes, order-significant (first-match-wins when a node has multiple outgoing edges).</param>
/// <param name="Description">Human-readable description.</param>
/// <param name="Labels">K/V metadata for filtering + organising in the registry.</param>
/// <param name="Annotations">Free-form operator-visible metadata not indexed by the registry.</param>
public sealed record AgentGraphManifest(
    string Id,
    string Version,
    string Entry,
    IReadOnlyList<GraphNode> Nodes,
    IReadOnlyList<GraphEdge> Edges,
    string? Description = null,
    IReadOnlyDictionary<string, string>? Labels = null,
    IReadOnlyDictionary<string, string>? Annotations = null)
{
    /// <summary>
    /// Optional JSON Schema describing the shape of graph state. Required for
    /// YAML-authored graphs that use declarative <see cref="GraphEdgePredicate"/>
    /// or <see cref="GraphEdgeEffect"/> entries — the validator uses the schema
    /// to check that referenced property names exist + have the expected type.
    /// Optional for code-first <see cref="IAgentGraph{TState}"/> consumers.
    /// </summary>
    public JsonElement? StateSchema { get; init; }

    /// <summary>
    /// Hard ceiling on super-step count per invocation. Prevents runaway cycles.
    /// Defaults to 1000 (matching LangGraph's <c>recursion_limit</c>) when null.
    /// Exceeding this throws <see cref="GraphRecursionException"/>.
    /// </summary>
    public int? MaxSteps { get; init; }
}

/// <summary>
/// One node in an <see cref="AgentGraphManifest"/>.
/// </summary>
/// <remarks>
/// Four kinds ship in v0.9:
/// <list type="bullet">
///   <item><description><c>Agent</c> — invokes an agent registered in <see cref="IAgentRegistry"/>. <see cref="Ref"/> is required; <see cref="HandlerRef"/> is ignored.</description></item>
///   <item><description><c>Code</c> — invokes a DI-resolved handler implementing <see cref="IGraphCodeNode"/>. <see cref="HandlerRef"/> is required; <see cref="Ref"/> is ignored.</description></item>
///   <item><description><c>Interrupt</c> — emits a <see cref="GraphInterrupted"/> event and pauses the graph; the caller resumes via <see cref="IAgentGraph{T}.InvokeAsync"/> with a matching checkpoint. Neither <see cref="Ref"/> nor <see cref="HandlerRef"/> is used.</description></item>
///   <item><description><c>End</c> — terminal node; reaching it completes the graph. No further edges are evaluated.</description></item>
/// </list>
/// Consumers pattern-match on <see cref="Kind"/>. Other kinds can land additively in a future pillar.
/// </remarks>
/// <param name="Id">Unique-within-graph identifier. Matches edge endpoints by string.</param>
/// <param name="Kind">One of <c>"Agent"</c>, <c>"Code"</c>, <c>"Interrupt"</c>, <c>"End"</c>.</param>
/// <param name="Ref">Reference to a registered agent, for <c>Agent</c>-kind nodes.</param>
/// <param name="HandlerRef">Reference to a DI-resolved code handler, for <c>Code</c>-kind nodes.</param>
/// <param name="StateBindings">Describes how graph state projects into/out of this node's invocation.</param>
/// <param name="InterruptReason">Operator-readable reason surfaced on <see cref="GraphInterrupted.Reason"/>; optional for <c>Interrupt</c>-kind nodes.</param>
public sealed record GraphNode(
    string Id,
    string Kind,
    GraphAgentRef? Ref = null,
    GraphHandlerRef? HandlerRef = null,
    GraphStateBindings? StateBindings = null,
    string? InterruptReason = null);

/// <summary>Reference to an agent in <see cref="IAgentRegistry"/> for <c>Agent</c>-kind nodes.</summary>
/// <param name="Id">Agent id.</param>
/// <param name="Version">Agent version. Null resolves to latest.</param>
/// <param name="RuntimeUrl">Absolute http/https URL of the remote runtime hosting this agent. Null = local (resolved via <see cref="IAgentRegistry"/>).</param>
public sealed record GraphAgentRef(string Id, string? Version = null, string? RuntimeUrl = null);

/// <summary>
/// Reference to a DI-resolved code handler. Matches <see cref="AgentHandlerRef"/>'s
/// shape for consistency.
/// </summary>
/// <param name="TypeName">Fully-qualified type name of the handler class.</param>
/// <param name="AssemblyName">Optional assembly name. Null when the runtime resolves by type-name alone.</param>
public sealed record GraphHandlerRef(string TypeName, string? AssemblyName = null);

/// <summary>
/// Describes how graph state projects into a node's invocation and how the node's
/// output merges back into graph state.
/// </summary>
/// <param name="Input">
/// Graph-state keys to read. For <c>Agent</c>-kind nodes, these keys become metadata
/// on the <see cref="AgentInvocationRequest"/>. For <c>Code</c>-kind nodes, the
/// handler receives a dictionary subset keyed by these names.
/// </param>
/// <param name="Output">
/// Graph-state keys to write from the node's output. For <c>Agent</c>-kind nodes,
/// the agent must declare a matching <see cref="AgentManifest.OutputSchema"/> with
/// these fields; the runtime extracts them from the structured assistant response.
/// For <c>Code</c>-kind nodes, the handler's output dictionary is filtered to these keys.
/// </param>
public sealed record GraphStateBindings(
    IReadOnlyList<string>? Input = null,
    IReadOnlyList<string>? Output = null);

/// <summary>
/// One edge in an <see cref="AgentGraphManifest"/>. Edges from the same <see cref="From"/>
/// are evaluated in manifest order — first matching predicate wins. At least one
/// always-true edge per source node is a convention for reachability of <c>End</c>.
/// </summary>
/// <param name="From">Source node id.</param>
/// <param name="To">Target node id.</param>
/// <param name="When">Predicate. Null means always-true.</param>
/// <param name="OnTraverse">Side-effect applied to graph state when the edge is taken.</param>
public sealed record GraphEdge(
    string From,
    string To,
    GraphEdgePredicate? When = null,
    GraphEdgeEffect? OnTraverse = null);
