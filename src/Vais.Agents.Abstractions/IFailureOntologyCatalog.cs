// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents;

/// <summary>
/// Read-only vocabulary of failure concepts. Provides the <em>taxonomy-north</em> half of the
/// ontology-grounded diagnostic layer: a stable, runtime-owned set of concept names that
/// <see cref="RunHealthSignal.ConceptName"/> is stamped against and that the diagnostic MCP
/// serves as the <c>vais-ontology://Failure</c> resource.
/// <para>
/// The default implementation (<see cref="AutoDerivedFailureOntologyCatalog"/>) auto-derives
/// the base taxonomy from <see cref="RunHealthSignalKind"/>. The overlay-aware implementation
/// (<c>OverlaidFailureOntologyCatalog</c> in <c>Vais.Agents.Control.Manifests.Json</c>) merges
/// deployment-local <see cref="FailureOntologyOverlay"/> JSON over the base.
/// </para>
/// </summary>
public interface IFailureOntologyCatalog
{
    /// <summary>Version string identifying the base taxonomy and any applied overlay.</summary>
    string OntologyVersion { get; }

    /// <summary>
    /// All concepts in this catalog, including overlay-added sub-concepts.
    /// Ordered: base mechanical concepts first, then quality seeds, then overlay additions.
    /// </summary>
    IReadOnlyCollection<FailureConcept> Concepts { get; }

    /// <summary>
    /// Returns the concept with <paramref name="conceptName"/>, or <see langword="null"/> if
    /// not found. The lookup is case-sensitive (concept names are stable identifiers).
    /// </summary>
    FailureConcept? Get(string conceptName);

    /// <summary>
    /// Returns the base <see cref="FailureConcept"/> whose <see cref="FailureConcept.SourceKinds"/>
    /// includes <paramref name="kind"/>, or <see langword="null"/> if no mapping exists.
    /// Used to stamp <see cref="RunHealthSignal.ConceptName"/> at write time.
    /// </summary>
    FailureConcept? FromSignalKind(RunHealthSignalKind kind);

    /// <summary>
    /// Walks the <see cref="FailureConcept.ParentName"/> chain to determine whether
    /// <paramref name="candidateName"/> is the same as <paramref name="filterName"/> or
    /// is a descendant of it. Used by concept-filtered eval assertions.
    /// </summary>
    bool IsMatchOrDescendant(string candidateName, string filterName);
}
