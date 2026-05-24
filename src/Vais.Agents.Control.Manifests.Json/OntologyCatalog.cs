// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;

namespace Vais.Agents.Control.Manifests;

/// <summary>
/// Builds and exposes the merged ontology (base + overlay). The base is the
/// <c>contracts/ontology/base-ontology.json</c> artifact produced by
/// <see cref="ManifestJsonSchemaGenerator.GenerateBaseOntology"/>; the overlay is a
/// deployment-local <see cref="OntologyOverlay"/> loaded by the caller.
/// Use <see cref="OntologyCatalog.Build"/> to construct an instance.
/// </summary>
public sealed class OntologyCatalog : IOntologyCatalog
{
    private readonly IReadOnlyDictionary<string, KindOntologyEntry> _entries;

    private OntologyCatalog(
        string ontologyVersion,
        IReadOnlyDictionary<string, KindOntologyEntry> entries,
        IReadOnlyList<RecipeEntry> recipes)
    {
        OntologyVersion = ontologyVersion;
        _entries = entries;
        Recipes = recipes;
        Kinds = [.. entries.Keys];
    }

    /// <inheritdoc/>
    public string OntologyVersion { get; }

    /// <inheritdoc/>
    public IReadOnlyList<string> Kinds { get; }

    /// <inheritdoc/>
    public IReadOnlyList<RecipeEntry> Recipes { get; }

    /// <inheritdoc/>
    public KindOntologyEntry Get(string kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        return _entries.TryGetValue(kind, out var entry) ? entry
            : throw new KeyNotFoundException($"Kind '{kind}' is not registered in the ontology catalog.");
    }

    /// <inheritdoc/>
    public bool TryGet(string kind, out KindOntologyEntry entry)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        return _entries.TryGetValue(kind, out entry!);
    }

    // ── Builder ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds the catalog from the <c>base-ontology.json</c> embedded in this assembly
    /// and an optional <paramref name="overlay"/>. Preferred for runtime use (no file-system path needed).
    /// </summary>
    public static OntologyCatalog BuildFromEmbeddedBase(OntologyOverlay? overlay = null)
    {
        var asm = typeof(OntologyCatalog).Assembly;
        using var stream = asm.GetManifestResourceStream("Vais.Agents.Control.Manifests.base-ontology.json")
            ?? throw new InvalidOperationException("Embedded resource 'base-ontology.json' not found in Vais.Agents.Control.Manifests.Json.");
        using var reader = new System.IO.StreamReader(stream);
        return Build(reader.ReadToEnd(), overlay);
    }

    /// <summary>
    /// Builds the catalog from the <paramref name="baseOntologyJson"/> string (the content of
    /// <c>contracts/ontology/base-ontology.json</c>) and an optional <paramref name="overlay"/>.
    /// Merge is additive and deterministic: overlay values win over base for descriptions and
    /// tags; recipes come from the overlay only. A null overlay is treated as
    /// <see cref="OntologyOverlay.Empty"/> (base-only mode).
    /// </summary>
    public static OntologyCatalog Build(string baseOntologyJson, OntologyOverlay? overlay = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseOntologyJson);
        overlay ??= OntologyOverlay.Empty;

        using var doc = JsonDocument.Parse(baseOntologyJson);
        var root = doc.RootElement;

        var version = root.TryGetProperty("ontologyVersion", out var ver) ? (ver.GetString() ?? "unknown") : "unknown";
        var kindsEl = root.GetProperty("kinds");

        var entries = new Dictionary<string, KindOntologyEntry>(StringComparer.Ordinal);

        foreach (var kindProp in kindsEl.EnumerateObject())
        {
            var kindName = kindProp.Name;
            var kindEl = kindProp.Value;
            var kindOverlay = overlay.ForKind(kindName);
            entries[kindName] = MergeKindEntry(kindName, kindEl, kindOverlay, version, overlay.Recipes ?? []);
        }

        // Add manual-concept kinds from the overlay that have no base schema entry.
        if (overlay.Kinds is not null)
        {
            foreach (var (kindName, ko) in overlay.Kinds)
            {
                if (!entries.ContainsKey(kindName) && ko.ManualConcept is not null)
                {
                    entries[kindName] = new KindOntologyEntry(
                        Kind: kindName,
                        Description: ko.Description ?? ko.ManualConcept,
                        RequiredFields: [],
                        Fields: new Dictionary<string, OntologyFieldInfo>(),
                        CrossRefs: [],
                        Tags: SortedDistinct(ko.Tags ?? []),
                        Recipes: FilterRecipes(kindName, overlay.Recipes ?? []),
                        ManualConcept: ko.ManualConcept,
                        OntologyVersion: version);
                }
            }
        }

        var globalRecipes = overlay.Recipes?.ToList() ?? (IReadOnlyList<RecipeEntry>)[];
        return new OntologyCatalog(version, entries, globalRecipes);
    }

    private static KindOntologyEntry MergeKindEntry(
        string kind,
        JsonElement kindEl,
        KindOverlay kindOverlay,
        string version,
        IReadOnlyList<RecipeEntry> overlayRecipes)
    {
        // Description: overlay wins
        var baseDesc = kindEl.TryGetProperty("description", out var dEl) ? (dEl.GetString() ?? "") : "";
        var description = kindOverlay.Description ?? baseDesc;

        // Required fields
        var required = new List<string>();
        if (kindEl.TryGetProperty("constraints", out var constraintsEl)
            && constraintsEl.TryGetProperty("required", out var reqEl))
        {
            foreach (var r in reqEl.EnumerateArray())
                if (r.GetString() is { } s) required.Add(s);
        }

        // Fields
        var fields = new Dictionary<string, OntologyFieldInfo>(StringComparer.Ordinal);
        if (kindEl.TryGetProperty("fields", out var fieldsEl))
        {
            foreach (var fieldProp in fieldsEl.EnumerateObject())
            {
                var fieldName = fieldProp.Name;
                var fieldEl = fieldProp.Value;
                var fieldOverlay = kindOverlay.Fields?.TryGetValue(fieldName, out var fo) == true ? fo : null;

                var fieldType = fieldEl.TryGetProperty("type", out var tEl) ? (tEl.GetString() ?? "any") : "any";
                var fieldBaseDesc = fieldEl.TryGetProperty("description", out var fdEl) ? (fdEl.GetString() ?? "") : "";
                var fieldDesc = fieldOverlay?.Description ?? fieldBaseDesc;

                var enumValues = default(List<string>);
                if (fieldEl.TryGetProperty("enum", out var enumEl))
                {
                    enumValues = [];
                    foreach (var ev in enumEl.EnumerateArray())
                        if (ev.GetString() is { } ev2) enumValues.Add(ev2);
                }

                var fieldTags = SortedDistinct(fieldOverlay?.Tags ?? []);
                fields[fieldName] = new OntologyFieldInfo(fieldName, fieldType, fieldDesc, enumValues, fieldTags);
            }
        }

        // Cross-refs
        var crossRefs = new List<CrossRefEdge>();
        if (kindEl.TryGetProperty("crossRefs", out var crossRefsEl))
        {
            foreach (var crEl in crossRefsEl.EnumerateArray())
            {
                var field = crEl.TryGetProperty("field", out var fEl) ? fEl.GetString() : null;
                var targetKind = crEl.TryGetProperty("targetKind", out var tkEl) ? tkEl.GetString() : null;
                var cardinality = crEl.TryGetProperty("cardinality", out var cEl) ? cEl.GetString() : null;
                if (field is not null && targetKind is not null && cardinality is not null)
                    crossRefs.Add(new CrossRefEdge(field, targetKind, cardinality));
            }
        }

        // Tags: merge base (none in current format) + overlay
        var tags = SortedDistinct(kindOverlay.Tags ?? []);

        var manualConcept = kindOverlay.ManualConcept;

        return new KindOntologyEntry(
            Kind: kind,
            Description: description,
            RequiredFields: required,
            Fields: fields,
            CrossRefs: crossRefs,
            Tags: tags,
            Recipes: FilterRecipes(kind, overlayRecipes),
            ManualConcept: manualConcept,
            OntologyVersion: version);
    }

    private static IReadOnlyList<RecipeEntry> FilterRecipes(string kind, IReadOnlyList<RecipeEntry> all)
        => all.Where(r => r.Steps.Any(s => string.Equals(s.Kind, kind, StringComparison.Ordinal))).ToList();

    private static IReadOnlyList<string> SortedDistinct(IEnumerable<string> source)
    {
        var set = new SortedSet<string>(source, StringComparer.Ordinal);
        return [.. set];
    }
}
