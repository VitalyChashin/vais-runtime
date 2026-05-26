// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Collections.Concurrent;
using System.Text.Json;

namespace Vais.Agents.Control.Manifests;

/// <summary>
/// Deployment-supplied domain-ontology artifact bound to a virtual MCP server via
/// <see cref="McpServerManifest.OntologyRef"/>. Carries per-tool descriptions, capability /
/// risk tags, and typed cross-references that the south cartridge (Plan C1-9..C1-11) uses to
/// shape <c>tools/list</c> responses and to validate / enrich tool calls.
/// </summary>
/// <remarks>
/// South analogue of <see cref="OntologyOverlay"/>: the loader machinery is OSS, the
/// artifact content stays deployment-local and is never checked into <c>agentic/</c>.
/// </remarks>
public sealed record DomainOntologyArtifact
{
    /// <summary>Version of this artifact (deployment-supplied; carried into telemetry via the binding).</summary>
    public string OntologyVersion { get; init; } = "0";

    /// <summary>Per-tool domain concepts keyed by the projected tool name (the agent-visible name).</summary>
    public IReadOnlyDictionary<string, DomainConcept>? Tools { get; init; }

    /// <summary>Returns the domain concept for <paramref name="toolName"/>, or null when the tool is not annotated.</summary>
    public DomainConcept? ForTool(string toolName)
        => Tools is not null && Tools.TryGetValue(toolName, out var c) ? c : null;

    /// <summary>An artifact with no entries — used when the binding resolves to nothing (graceful fallback).</summary>
    public static readonly DomainOntologyArtifact Empty = new();
}

/// <summary>One tool's worth of domain-ontology annotation.</summary>
public sealed record DomainConcept
{
    /// <summary>Description override. Null = use the upstream tool's description.</summary>
    public string? Description { get; init; }

    /// <summary>Capability / risk tags (e.g. <c>risk:Destructive</c>, <c>category:network</c>). Empty / null = no tags.</summary>
    public IReadOnlyList<string>? Tags { get; init; }

    /// <summary>Typed cross-references from this tool's arguments to other concepts in the binding.</summary>
    public IReadOnlyList<DomainCrossRef>? CrossRefs { get; init; }
}

/// <summary>Typed cross-reference edge from a tool argument to another concept.</summary>
/// <param name="FieldPath">Dot-path into the tool's argument object (e.g. <c>url</c>, <c>params.target</c>).</param>
/// <param name="TargetConceptName">The concept the value points at (a tool name in the same binding, or an external concept).</param>
/// <param name="Cardinality">Optional <c>one</c> | <c>many</c> marker.</param>
public sealed record DomainCrossRef(
    string FieldPath,
    string TargetConceptName,
    string? Cardinality = null);

/// <summary>
/// Loads a <see cref="DomainOntologyArtifact"/> from a JSON file, JSON string, or a directory
/// of <c>*.domain-ontology.json</c> files. Mirrors <see cref="OntologyOverlayLoader"/>;
/// deployment-local content is supplied by the caller (never checked into <c>agentic/</c>).
/// </summary>
public static class DomainOntologyArtifactLoader
{
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Loads an artifact from <paramref name="path"/>. Missing or null path = <see cref="DomainOntologyArtifact.Empty"/>.</summary>
    public static DomainOntologyArtifact LoadFromFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return DomainOntologyArtifact.Empty;
        return LoadFromJson(File.ReadAllText(path));
    }

    /// <summary>Parses an artifact from a JSON string. Null / empty = <see cref="DomainOntologyArtifact.Empty"/>.</summary>
    public static DomainOntologyArtifact LoadFromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return DomainOntologyArtifact.Empty;
        return JsonSerializer.Deserialize<DomainOntologyArtifact>(json, ReadOptions) ?? DomainOntologyArtifact.Empty;
    }

    /// <summary>
    /// Loads every <c>*.domain-ontology.json</c> file under <paramref name="directory"/> into a map
    /// keyed by file stem (without the <c>.domain-ontology.json</c> suffix). A non-existent
    /// directory yields an empty map; malformed files are skipped silently to keep deployer
    /// onboarding forgiving (use <see cref="LoadFromFile"/> directly for strict validation).
    /// </summary>
    public static IReadOnlyDictionary<string, DomainOntologyArtifact> LoadAllFromDirectory(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return new Dictionary<string, DomainOntologyArtifact>(StringComparer.Ordinal);

        var map = new Dictionary<string, DomainOntologyArtifact>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(directory, "*.domain-ontology.json"))
        {
            var fileName = Path.GetFileName(path);
            var stem = fileName[..^".domain-ontology.json".Length];
            try { map[stem] = LoadFromJson(File.ReadAllText(path)); }
            catch (JsonException) { /* skip malformed; deployer will fix once they notice the missing cartridge */ }
        }
        return map;
    }
}

/// <summary>
/// Registry of named <see cref="DomainOntologyArtifact"/> instances. The south cartridge
/// resolves <see cref="McpServerManifest.OntologyRef"/> through this seam; an unknown ref
/// returns <c>null</c> and the cartridge degrades to passthrough.
/// </summary>
public interface IDomainOntologyArtifactRegistry
{
    /// <summary>Get the artifact registered under <paramref name="name"/>, or <c>null</c> if none.</summary>
    DomainOntologyArtifact? Get(string name);

    /// <summary>Register (or replace) an artifact under <paramref name="name"/>. Thread-safe.</summary>
    void Register(string name, DomainOntologyArtifact artifact);

    /// <summary>All registered names. Stable enumeration not guaranteed.</summary>
    IReadOnlyCollection<string> Names { get; }
}

/// <summary>In-memory <see cref="IDomainOntologyArtifactRegistry"/> populated programmatically or by the loader.</summary>
public sealed class InMemoryDomainOntologyArtifactRegistry : IDomainOntologyArtifactRegistry
{
    private readonly ConcurrentDictionary<string, DomainOntologyArtifact> _entries = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public DomainOntologyArtifact? Get(string name) => _entries.TryGetValue(name, out var v) ? v : null;

    /// <inheritdoc />
    public void Register(string name, DomainOntologyArtifact artifact)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(artifact);
        _entries[name] = artifact;
    }

    /// <inheritdoc />
    public IReadOnlyCollection<string> Names => (IReadOnlyCollection<string>)_entries.Keys;

    /// <summary>
    /// Convenience: bulk-register every artifact from a name → artifact map (e.g. the output of
    /// <see cref="DomainOntologyArtifactLoader.LoadAllFromDirectory"/>).
    /// </summary>
    public void RegisterAll(IReadOnlyDictionary<string, DomainOntologyArtifact> artifacts)
    {
        ArgumentNullException.ThrowIfNull(artifacts);
        foreach (var (name, artifact) in artifacts) Register(name, artifact);
    }
}
