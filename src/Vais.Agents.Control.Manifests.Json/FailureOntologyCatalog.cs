// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vais.Agents.Control.Manifests;

/// <summary>
/// Overlay-aware implementation of <see cref="IFailureOntologyCatalog"/>. Merges a
/// deployment-local <see cref="FailureOntologyOverlay"/> over the auto-derived base from
/// <see cref="AutoDerivedFailureOntologyCatalog"/>. Overlay concepts extend or replace base
/// entries (same <see cref="FailureConcept.Name"/> wins for the overlay). Overlay sub-concepts
/// (those with a non-null <see cref="FailureConcept.ParentName"/>) are additive.
/// </summary>
public sealed class OverlaidFailureOntologyCatalog : IFailureOntologyCatalog
{
    private readonly IReadOnlyDictionary<string, FailureConcept> _all;
    private readonly IReadOnlyDictionary<RunHealthSignalKind, FailureConcept> _byKind;
    private readonly IReadOnlyDictionary<string, FailureAttributionOverlay> _attributions;

    /// <summary>Builds the catalog from the given overlay merged over the auto-derived base.</summary>
    public OverlaidFailureOntologyCatalog(FailureOntologyOverlay overlay)
    {
        ArgumentNullException.ThrowIfNull(overlay);
        var base_ = AutoDerivedFailureOntologyCatalog.Instance;

        var all = new Dictionary<string, FailureConcept>(
            base_.Concepts.ToDictionary(c => c.Name, StringComparer.Ordinal),
            StringComparer.Ordinal);

        if (overlay.Concepts is not null)
        {
            foreach (var c in overlay.Concepts)
                // Normalize null SourceKinds (may occur when JSON omits the field) to empty list.
                all[c.Name] = c.SourceKinds is null ? c with { SourceKinds = [] } : c;
        }

        _all = all;
        _attributions = overlay.Attributions
            ?? (IReadOnlyDictionary<string, FailureAttributionOverlay>)new Dictionary<string, FailureAttributionOverlay>();

        // For kind-based lookup, base catalog wins (only base concepts have SourceKinds).
        _byKind = base_.Concepts
            .Where(c => c.SourceKinds.Count > 0)
            .SelectMany(c => c.SourceKinds.Select(k => (k, c)))
            .ToDictionary(t => t.k, t => t.c);

        OntologyVersion = $"overlaid-{base_.OntologyVersion}";
    }

    /// <inheritdoc/>
    public string OntologyVersion { get; }

    /// <inheritdoc/>
    public IReadOnlyCollection<FailureConcept> Concepts => [.. _all.Values];

    /// <inheritdoc/>
    public FailureConcept? Get(string conceptName) =>
        _all.TryGetValue(conceptName, out var c) ? c : null;

    /// <inheritdoc/>
    public FailureConcept? FromSignalKind(RunHealthSignalKind kind) =>
        _byKind.TryGetValue(kind, out var c) ? c : null;

    /// <inheritdoc/>
    public bool IsMatchOrDescendant(string candidateName, string filterName)
    {
        if (string.Equals(candidateName, filterName, StringComparison.Ordinal))
            return true;
        var current = Get(candidateName);
        while (current?.ParentName is not null)
        {
            if (string.Equals(current.ParentName, filterName, StringComparison.Ordinal))
                return true;
            current = Get(current.ParentName);
        }
        return false;
    }

    /// <inheritdoc/>
    public IReadOnlyList<(string AttributionPath, FailurePriorBody Prior)> GetPriorsForConcept(
        string conceptName)
    {
        var results = new List<(string, FailurePriorBody)>();
        foreach (var (path, attrOverlay) in _attributions)
        {
            if (attrOverlay.FailurePriors is null) continue;
            foreach (var prior in attrOverlay.FailurePriors)
            {
                if (string.Equals(prior.ConceptName, conceptName, StringComparison.Ordinal))
                    results.Add((path, prior));
            }
        }
        return results;
    }
}

/// <summary>
/// Hot-reloadable facade over <see cref="IFailureOntologyCatalog"/>. Mirrors
/// <see cref="HotReloadableOntologyCatalog"/> but targets the failure catalog.
/// The composition root registers a single instance as both
/// <see cref="IFailureOntologyCatalog"/> and <see cref="IFailureOntologyCatalogReloader"/>.
/// </summary>
/// <remarks>
/// Reads are lock-free (Volatile.Read on the inner reference). Reload is single-flight:
/// only one rebuild executes at a time; subsequent callers see the post-swap catalog on
/// the next read. The rebuild re-runs <see cref="FailureOntologyOverlayLoader.LoadAllFromDirectory"/>
/// against the configured overlay directory — the same directory-glob semantics used at startup.
/// </remarks>
public sealed class HotReloadableFailureOntologyCatalog
    : IFailureOntologyCatalog, IFailureOntologyCatalogReloader
{
    private IFailureOntologyCatalog _inner;
    private readonly string _overlayPath;
    private readonly object _reloadLock = new();

    /// <summary>Build the facade. <paramref name="initial"/> is the catalog at startup.</summary>
    public HotReloadableFailureOntologyCatalog(IFailureOntologyCatalog initial, string overlayPath)
    {
        ArgumentNullException.ThrowIfNull(initial);
        ArgumentException.ThrowIfNullOrWhiteSpace(overlayPath);
        _inner = initial;
        _overlayPath = overlayPath;
    }

    private IFailureOntologyCatalog Current => Volatile.Read(ref _inner);

    /// <inheritdoc />
    public Task<IFailureOntologyCatalog> ReloadAsync(CancellationToken cancellationToken = default)
    {
        IFailureOntologyCatalog rebuilt;
        lock (_reloadLock)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var overlay = FailureOntologyOverlayLoader.LoadAllFromDirectory(_overlayPath);
            rebuilt = new OverlaidFailureOntologyCatalog(overlay);
            Volatile.Write(ref _inner, rebuilt);
        }
        return Task.FromResult(rebuilt);
    }

    // ── IFailureOntologyCatalog forwarding ────────────────────────────────────

    /// <inheritdoc />
    public string OntologyVersion => Current.OntologyVersion;

    /// <inheritdoc />
    public IReadOnlyCollection<FailureConcept> Concepts => Current.Concepts;

    /// <inheritdoc />
    public FailureConcept? Get(string conceptName) => Current.Get(conceptName);

    /// <inheritdoc />
    public FailureConcept? FromSignalKind(RunHealthSignalKind kind) => Current.FromSignalKind(kind);

    /// <inheritdoc />
    public bool IsMatchOrDescendant(string candidateName, string filterName)
        => Current.IsMatchOrDescendant(candidateName, filterName);

    /// <inheritdoc />
    public IReadOnlyList<(string AttributionPath, FailurePriorBody Prior)> GetPriorsForConcept(
        string conceptName) => Current.GetPriorsForConcept(conceptName);
}

/// <summary>
/// Loads a <see cref="FailureOntologyOverlay"/> from a JSON file or JSON string. File glob:
/// <c>*.failure-ontology.json</c>. Mirrors <c>OntologyOverlayLoader</c>'s static-method shape.
/// </summary>
public static class FailureOntologyOverlayLoader
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Loads an overlay from a JSON file at <paramref name="path"/>.</summary>
    public static FailureOntologyOverlay LoadFromFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var json = File.ReadAllText(path);
        return LoadFromJson(json);
    }

    /// <summary>Loads an overlay from a raw JSON string.</summary>
    public static FailureOntologyOverlay LoadFromJson(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        return JsonSerializer.Deserialize<FailureOntologyOverlay>(json, JsonOpts)
               ?? FailureOntologyOverlay.Empty;
    }

    /// <summary>
    /// Loads and merges all <c>*.failure-ontology.json</c> files in <paramref name="directory"/>,
    /// combining their concepts and severity rules additively.
    /// </summary>
    public static FailureOntologyOverlay LoadAllFromDirectory(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (!Directory.Exists(directory))
            return FailureOntologyOverlay.Empty;

        var files = Directory.GetFiles(directory, "*.failure-ontology.json", SearchOption.TopDirectoryOnly);
        if (files.Length == 0)
            return FailureOntologyOverlay.Empty;

        var concepts = new List<FailureConcept>();
        var rules = new List<FailureSeverityRule>();
        var attributions = new Dictionary<string, FailureAttributionOverlay>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            var overlay = LoadFromFile(file);
            if (overlay.Concepts is not null) concepts.AddRange(overlay.Concepts);
            if (overlay.SeverityRules is not null) rules.AddRange(overlay.SeverityRules);
            if (overlay.Attributions is not null)
            {
                foreach (var (path, attr) in overlay.Attributions)
                {
                    if (!attributions.TryGetValue(path, out var existing) || existing.FailurePriors is null)
                        attributions[path] = attr;
                    else if (attr.FailurePriors is not null)
                        attributions[path] = new FailureAttributionOverlay(
                            [.. existing.FailurePriors, .. attr.FailurePriors]);
                }
            }
        }

        return new FailureOntologyOverlay(
            Concepts: concepts.Count > 0 ? concepts : null,
            SeverityRules: rules.Count > 0 ? rules : null,
            Attributions: attributions.Count > 0 ? attributions : null);
    }
}
