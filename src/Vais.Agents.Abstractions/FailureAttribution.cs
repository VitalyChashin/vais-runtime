// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Collections.Concurrent;

namespace Vais.Agents;

/// <summary>
/// Deployment-local attribution artifact that maps tool names and agent names to failure
/// concepts from the shared <see cref="IFailureOntologyCatalog"/> taxonomy. Resolved via
/// <see cref="IFailureAttributionRegistry"/>. Content is deployment-specific; types are OSS.
/// Mirrors <c>DomainOntologyArtifact</c> from Plan C1.
/// </summary>
/// <param name="OntologyVersion">Optional version string for change tracking.</param>
/// <param name="Tools">Per-tool annotations keyed by the projected tool name.</param>
/// <param name="Agents">Per-agent annotations keyed by agent ID.</param>
public sealed record FailureAttributionArtifact(
    string? OntologyVersion = null,
    IReadOnlyDictionary<string, FailureToolAnnotation>? Tools = null,
    IReadOnlyDictionary<string, FailureAgentAnnotation>? Agents = null)
{
    /// <summary>Returns the annotation for a tool, or <see langword="null"/> if not annotated.</summary>
    public FailureToolAnnotation? ForTool(string toolName) =>
        Tools?.TryGetValue(toolName, out var a) == true ? a : null;

    /// <summary>Returns the annotation for an agent, or <see langword="null"/> if not annotated.</summary>
    public FailureAgentAnnotation? ForAgent(string agentId) =>
        Agents?.TryGetValue(agentId, out var a) == true ? a : null;
}

/// <summary>
/// Per-tool annotation in a <see cref="FailureAttributionArtifact"/>. Provides a concept
/// name override (sub-concept refinement over the base taxonomy) and optional routing hints.
/// </summary>
/// <param name="Concept">Failure concept name from the shared taxonomy (e.g. <c>McpToolError/AuthExpired</c>). Null = use base catalog concept.</param>
/// <param name="McpServerId">The virtual MCP server ID this tool belongs to, for attribution path construction.</param>
/// <param name="Tags">Diagnostic tags applied to signals attributed via this annotation.</param>
public sealed record FailureToolAnnotation(
    string? Concept = null,
    string? McpServerId = null,
    IReadOnlyList<string>? Tags = null);

/// <summary>Per-agent annotation in a <see cref="FailureAttributionArtifact"/>.</summary>
/// <param name="Concept">Failure concept name for agent-level failures. Null = use base catalog concept.</param>
public sealed record FailureAgentAnnotation(
    string? Concept = null);

// ── Registry ─────────────────────────────────────────────────────────────────

/// <summary>
/// Resolves a <c>FailureOntologyRef</c> string to a <see cref="FailureAttributionArtifact"/>.
/// Registered as a singleton; populated at startup from <c>*.failure-attribution.json</c> files
/// via <c>FailureAttributionArtifactLoader</c> in <c>Vais.Agents.Control.Manifests.Json</c>.
/// </summary>
public interface IFailureAttributionRegistry
{
    /// <summary>Returns the artifact for <paramref name="name"/>, or <see langword="null"/> if not registered.</summary>
    FailureAttributionArtifact? Get(string name);
    /// <summary>Registers an artifact under <paramref name="name"/>.</summary>
    void Register(string name, FailureAttributionArtifact artifact);
    /// <summary>All registered artifact names.</summary>
    IReadOnlyList<string> Names { get; }
}

/// <summary>Thread-safe in-memory <see cref="IFailureAttributionRegistry"/>.</summary>
public sealed class InMemoryFailureAttributionRegistry : IFailureAttributionRegistry
{
    private readonly ConcurrentDictionary<string, FailureAttributionArtifact> _store =
        new(StringComparer.Ordinal);

    /// <inheritdoc/>
    public FailureAttributionArtifact? Get(string name) =>
        _store.TryGetValue(name, out var a) ? a : null;

    /// <inheritdoc/>
    public void Register(string name, FailureAttributionArtifact artifact) =>
        _store[name] = artifact;

    /// <inheritdoc/>
    public IReadOnlyList<string> Names => [.. _store.Keys];

    /// <summary>Bulk-registers all entries from <paramref name="map"/>.</summary>
    public void RegisterAll(IReadOnlyDictionary<string, FailureAttributionArtifact> map)
    {
        foreach (var (k, v) in map)
            _store[k] = v;
    }
}

// ── Index (agentId → FailureOntologyRef) ─────────────────────────────────────

/// <summary>
/// Lightweight in-memory index populated at agent activation time by
/// <c>AgentManifestTranslator</c>. Maps agent IDs to their bound <c>FailureOntologyRef</c>
/// string so <c>RunHealthSignalSubscriber</c> can resolve the artifact without reading the
/// manifest. NOT durable — rebuilt on silo restart at first agent activation.
/// </summary>
public interface IFailureAttributionIndex
{
    /// <summary>Records that <paramref name="agentId"/> is bound to <paramref name="failureOntologyRef"/>.</summary>
    void Register(string agentId, string failureOntologyRef);
    /// <summary>Looks up the ref for <paramref name="agentId"/>. Returns <see langword="false"/> when not indexed.</summary>
    bool TryGet(string agentId, out string failureOntologyRef);
}

/// <summary>Thread-safe in-memory <see cref="IFailureAttributionIndex"/>.</summary>
public sealed class InMemoryFailureAttributionIndex : IFailureAttributionIndex
{
    private readonly ConcurrentDictionary<string, string> _store = new(StringComparer.Ordinal);

    /// <inheritdoc/>
    public void Register(string agentId, string failureOntologyRef) =>
        _store[agentId] = failureOntologyRef;

    /// <inheritdoc/>
    public bool TryGet(string agentId, out string failureOntologyRef) =>
        _store.TryGetValue(agentId, out failureOntologyRef!);
}
