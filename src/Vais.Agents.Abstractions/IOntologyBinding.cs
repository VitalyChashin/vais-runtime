// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents;

/// <summary>
/// Cross-cutting seam an <see cref="OntologyInterceptor"/> reads to make a decision without
/// knowing which transport it is observing. Concrete catalogs implement this — the existing
/// north <c>IOntologyCatalog</c> over the resource model, and the south
/// <see cref="IDomainOntologyCatalog"/> over a virtual MCP server's tools.
/// </summary>
/// <remarks>
/// The minimum-common surface: a version stamp, the set of concept names in the bound
/// ontology, and a name-keyed lookup that returns the tags, description, and typed
/// cross-references an interceptor needs to filter, rewrite, or validate. Concrete catalogs
/// expose richer surfaces (field type info, recipes, projection metadata); the seam keeps
/// only what is portable across north and south.
/// </remarks>
public interface IOntologyBinding
{
    /// <summary>
    /// Version of the bound ontology artifact (typically the catalog's content-hash or the
    /// overlay version). Carried into telemetry so a chain run can be correlated with the
    /// exact ontology snapshot that shaped it.
    /// </summary>
    string OntologyVersion { get; }

    /// <summary>All concept names registered in this binding.</summary>
    IReadOnlyList<string> ConceptNames { get; }

    /// <summary>
    /// Attempts to look up a concept by name. Returns <see langword="false"/> when the
    /// concept is not in this binding — callers must handle the unbound case as
    /// passthrough (do not synthesize a default entry).
    /// </summary>
    bool TryGetConcept(string conceptName, out OntologyConceptEntry entry);
}

/// <summary>
/// South-side marker for a domain ontology bound to a virtual MCP server's tool surface.
/// Distinct from north <c>IOntologyCatalog</c> so DI scoping and lifetime can differ; both
/// satisfy <see cref="IOntologyBinding"/> so an interceptor written against the seam works
/// against either.
/// </summary>
/// <remarks>
/// C1-3 ships the interface only — the loader, the projected concrete catalog, and the
/// per-virtual-server binding plumbing land in C1-7 / C1-8 alongside the south cartridge.
/// </remarks>
public interface IDomainOntologyCatalog : IOntologyBinding
{
}

/// <summary>
/// Cross-cutting concept entry the binding seam exposes. Concrete catalogs project from
/// their richer per-domain entry type (north <c>KindOntologyEntry</c>, south
/// per-tool entry) onto this minimum surface.
/// </summary>
public sealed record OntologyConceptEntry
{
    /// <summary>The concept name (north = kind name, south = tool name).</summary>
    public required string Name { get; init; }

    /// <summary>Effective description, or <c>null</c> when the source did not supply one.</summary>
    public string? Description { get; init; }

    /// <summary>Tags attached to this concept (capability / risk markers, deployment-specific labels). Empty when none.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>Typed cross-references from this concept to other concepts in the binding. Empty when none.</summary>
    public IReadOnlyList<OntologyConceptCrossRef> CrossRefs { get; init; } = [];
}

/// <summary>Typed cross-reference edge surfaced by the binding seam.</summary>
/// <param name="FieldPath">The source field whose value points at the target (e.g. <c>spec.modelRef</c>).</param>
/// <param name="TargetConceptName">Name of the concept being pointed at.</param>
/// <param name="Cardinality">Optional cardinality marker (e.g. <c>one</c>, <c>many</c>) when the catalog records one.</param>
public sealed record OntologyConceptCrossRef(
    string FieldPath,
    string TargetConceptName,
    string? Cardinality = null);
