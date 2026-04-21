// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;

namespace Vais.Agents.Control.Kubernetes;

/// <summary>
/// The <c>.spec</c> subresource of an <see cref="AgentGraphEntity"/> custom resource.
/// Mirrors the field set of <see cref="AgentGraphManifest"/> — declarative specification
/// for a graph of agents that the operator hands to the control plane's
/// <c>IAgentGraphLifecycleManager.CreateAsync</c> / <c>UpdateAsync</c> verbs.
/// </summary>
/// <remarks>
/// The operator's internal <c>AgentGraphSpecProjector</c> maps this type field-by-field
/// onto <see cref="AgentGraphManifest"/>. Users author YAML against this type; consumers
/// never construct it directly.
/// </remarks>
public sealed class AgentGraphSpec
{
    /// <summary>Stable identifier — unique within the owning tenant. Matches <see cref="AgentGraphManifest.Id"/>.</summary>
    public string GraphId { get; set; } = string.Empty;

    /// <summary>Immutable version tag. Bumping this triggers an <c>UpdateAsync</c> against the control plane. Matches <see cref="AgentGraphManifest.Version"/>.</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>Id of the entry node. Must exist in <see cref="Nodes"/>. Matches <see cref="AgentGraphManifest.Entry"/>.</summary>
    public string Entry { get; set; } = string.Empty;

    /// <summary>All nodes in the graph. Ids unique within the manifest. Matches <see cref="AgentGraphManifest.Nodes"/>.</summary>
    public IList<GraphNode> Nodes { get; set; } = new List<GraphNode>();

    /// <summary>Edges between nodes, order-significant (first-match-wins). Matches <see cref="AgentGraphManifest.Edges"/>.</summary>
    public IList<GraphEdge> Edges { get; set; } = new List<GraphEdge>();

    /// <summary>Human-readable description for registries / UIs.</summary>
    public string? Description { get; set; }

    /// <summary>Registry-level key/value metadata. Distinct from <c>metadata.labels</c>; the former is graph-scope, the latter is K8s-scope.</summary>
    public IDictionary<string, string>? Labels { get; set; }

    /// <summary>Free-form annotations — operator-visible metadata not indexed by the registry. Distinct from <c>metadata.annotations</c>.</summary>
    public IDictionary<string, string>? Annotations { get; set; }

    /// <summary>
    /// Optional JSON Schema describing the shape of graph state. Preserved as arbitrary
    /// JSON via <c>x-kubernetes-preserve-unknown-fields</c>.
    /// </summary>
    public JsonElement? StateSchema { get; set; }

    /// <summary>Hard ceiling on super-step count per invocation. Null = runtime default (1000).</summary>
    public int? MaxSteps { get; set; }

    /// <summary>
    /// When <c>true</c>, deletion of the CR releases the finalizer without calling
    /// <c>EvictAsync</c> on the runtime. Graph state in the runtime persists for rebuild
    /// from a different source. When <c>false</c> (default), CR deletion triggers runtime eviction.
    /// </summary>
    public bool PreserveOnDelete { get; set; }
}
