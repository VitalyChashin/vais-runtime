// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Control.Manifests;

/// <summary>
/// <see cref="IDomainOntologyCatalog"/> implementation for the south cartridge. Merges a
/// <see cref="DomainOntologyArtifact"/> over a tool-projection scope: <see cref="ConceptNames"/>
/// is the projected tool name set; <see cref="TryGetConcept"/> layers annotations
/// (description override, tags, typed cross-refs) from the artifact when present, and returns
/// empty defaults when the tool is in the projection but not in the artifact.
/// </summary>
/// <remarks>
/// "Unknown tool = passthrough" — a tool name absent from the catalog scope (either no
/// projection or the artifact-only mode below) yields <c>false</c> from <see cref="TryGetConcept"/>,
/// which the cartridge interprets as "no shaping applied". Research §14.1 honest gap: the
/// projection is select+rename only, so this catalog is the only place per-tool semantics live.
/// </remarks>
public sealed class DomainOntologyCatalog : IDomainOntologyCatalog
{
    private readonly DomainOntologyArtifact _artifact;
    private readonly IReadOnlyList<string> _conceptNames;
    private readonly HashSet<string> _conceptSet;

    /// <summary>
    /// Build a catalog scoped to <paramref name="projectedToolNames"/> (the agent-visible
    /// tool names on a virtual server's projection). When <paramref name="projectedToolNames"/>
    /// is <see langword="null"/> or empty, the scope falls back to the artifact's annotated
    /// tools (useful for non-virtual servers where the runtime supplies the full tool list
    /// from upstream <c>tools/list</c> and only annotated tools matter).
    /// </summary>
    public DomainOntologyCatalog(DomainOntologyArtifact artifact, IReadOnlyList<string>? projectedToolNames = null)
    {
        _artifact = artifact ?? throw new ArgumentNullException(nameof(artifact));
        var scope = projectedToolNames is { Count: > 0 }
            ? projectedToolNames
            : (artifact.Tools is { Count: > 0 } ? [.. artifact.Tools.Keys] : (IReadOnlyList<string>)[]);
        _conceptNames = scope;
        _conceptSet = new HashSet<string>(scope, StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public string OntologyVersion => _artifact.OntologyVersion;

    /// <inheritdoc />
    public IReadOnlyList<string> ConceptNames => _conceptNames;

    /// <inheritdoc />
    public bool TryGetConcept(string conceptName, out OntologyConceptEntry entry)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conceptName);
        if (!_conceptSet.Contains(conceptName))
        {
            entry = null!;
            return false;
        }
        var annotation = _artifact.ForTool(conceptName);
        entry = new OntologyConceptEntry
        {
            Name = conceptName,
            Description = annotation?.Description,
            Tags = annotation?.Tags ?? [],
            CrossRefs = ProjectCrossRefs(annotation?.CrossRefs),
        };
        return true;
    }

    private static IReadOnlyList<OntologyConceptCrossRef> ProjectCrossRefs(IReadOnlyList<DomainCrossRef>? edges)
    {
        if (edges is null || edges.Count == 0) return [];
        var projected = new OntologyConceptCrossRef[edges.Count];
        for (var i = 0; i < edges.Count; i++)
        {
            var e = edges[i];
            projected[i] = new OntologyConceptCrossRef(e.FieldPath, e.TargetConceptName, e.Cardinality);
        }
        return projected;
    }
}
