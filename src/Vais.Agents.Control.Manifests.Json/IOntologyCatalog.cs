// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Control.Manifests;

/// <summary>
/// Per-kind ontology entry: merged view of the generated base ontology and the
/// deployment-local overlay. All string fields are non-null after merge (falling
/// back to the base value or an empty default when the overlay is absent).
/// </summary>
/// <param name="Kind">Manifest kind name (e.g. <c>Agent</c>).</param>
/// <param name="Description">Effective description — overlay wins over base when both are present.</param>
/// <param name="RequiredFields">Fields required in the spec (from the base constraints).</param>
/// <param name="Fields">Per-field type info + effective description, keyed by wire field name.</param>
/// <param name="CrossRefs">Typed cross-reference edges to other kinds.</param>
/// <param name="Tags">Merged capability/risk tags from base + overlay (deduplicated, sorted).</param>
/// <param name="Recipes">Authoring recipes that apply to this kind (from the overlay).</param>
/// <param name="ManualConcept">Manual concept text for schema-less kinds (e.g. Extension); null for schema-backed kinds.</param>
/// <param name="OntologyVersion">Ontology version string (from the base artifact).</param>
public sealed record KindOntologyEntry(
    string Kind,
    string Description,
    IReadOnlyList<string> RequiredFields,
    IReadOnlyDictionary<string, OntologyFieldInfo> Fields,
    IReadOnlyList<CrossRefEdge> CrossRefs,
    IReadOnlyList<string> Tags,
    IReadOnlyList<RecipeEntry> Recipes,
    string? ManualConcept,
    string OntologyVersion);

/// <summary>Type info + effective description for a single field within a kind's ontology entry.</summary>
/// <param name="FieldName">Wire field name.</param>
/// <param name="Type">JSON-schema-compatible type name (<c>string</c>, <c>integer</c>, <c>object</c>, <c>array</c>, <c>any</c>, …).</param>
/// <param name="Description">Effective description (overlay wins over base).</param>
/// <param name="EnumValues">Allowed values when the field is an enum; null otherwise.</param>
/// <param name="Tags">Tags for this field (from the overlay).</param>
public sealed record OntologyFieldInfo(
    string FieldName,
    string Type,
    string Description,
    IReadOnlyList<string>? EnumValues,
    IReadOnlyList<string> Tags);

/// <summary>
/// Read-only view of the merged ontology (generated base + deployment-local overlay).
/// The catalog is populated once at startup and is safe to read from any thread.
/// </summary>
public interface IOntologyCatalog
{
    /// <summary>Ontology version from the base artifact.</summary>
    string OntologyVersion { get; }

    /// <summary>
    /// Returns the merged ontology entry for <paramref name="kind"/>.
    /// Throws <see cref="KeyNotFoundException"/> for unknown kinds — callers should use
    /// <see cref="TryGet"/> when the kind is not guaranteed to be registered.
    /// </summary>
    KindOntologyEntry Get(string kind);

    /// <summary>
    /// Attempts to return the merged ontology entry for <paramref name="kind"/>.
    /// Returns <see langword="false"/> when the kind is not registered (e.g. <c>Extension</c>
    /// in base-only mode without an overlay concept).
    /// </summary>
    bool TryGet(string kind, out KindOntologyEntry entry);

    /// <summary>All registered kind names in the catalog (schema-backed + manual-concept kinds).</summary>
    IReadOnlyList<string> Kinds { get; }

    /// <summary>All recipes defined in the overlay (empty when no overlay is loaded).</summary>
    IReadOnlyList<RecipeEntry> Recipes { get; }
}
