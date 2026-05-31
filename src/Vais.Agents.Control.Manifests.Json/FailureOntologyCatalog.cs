// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;

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
                all[c.Name] = c;
        }

        _all = all;

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
}

/// <summary>
/// Loads a <see cref="FailureOntologyOverlay"/> from a JSON file or JSON string. File glob:
/// <c>*.failure-ontology.json</c>. Mirrors <c>OntologyOverlayLoader</c>'s static-method shape.
/// </summary>
public static class FailureOntologyOverlayLoader
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

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
        foreach (var file in files)
        {
            var overlay = LoadFromFile(file);
            if (overlay.Concepts is not null) concepts.AddRange(overlay.Concepts);
            if (overlay.SeverityRules is not null) rules.AddRange(overlay.SeverityRules);
        }

        return new FailureOntologyOverlay(
            Concepts: concepts.Count > 0 ? concepts : null,
            SeverityRules: rules.Count > 0 ? rules : null);
    }
}
