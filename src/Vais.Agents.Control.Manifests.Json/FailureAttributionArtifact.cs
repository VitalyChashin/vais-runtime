// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;

namespace Vais.Agents.Control.Manifests;

/// <summary>
/// Loads <see cref="FailureAttributionArtifact"/> instances from JSON files.
/// File glob: <c>*.failure-attribution.json</c>.
/// The artifact types and registry interfaces live in <c>Vais.Agents.Abstractions</c>;
/// this loader is the only file-I/O surface, kept in <c>Manifests.Json</c>.
/// Mirrors <c>DomainOntologyArtifactLoader</c>.
/// </summary>
public static class FailureAttributionArtifactLoader
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    /// <summary>Loads an artifact from a JSON file at <paramref name="path"/>.</summary>
    public static FailureAttributionArtifact LoadFromFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return LoadFromJson(File.ReadAllText(path));
    }

    /// <summary>Loads an artifact from a raw JSON string.</summary>
    public static FailureAttributionArtifact LoadFromJson(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        return JsonSerializer.Deserialize<FailureAttributionArtifact>(json, JsonOpts)
               ?? new FailureAttributionArtifact();
    }

    /// <summary>
    /// Loads all <c>*.failure-attribution.json</c> files in <paramref name="directory"/>,
    /// returning a map keyed by file stem (the ref name, without the <c>.failure-attribution</c> suffix).
    /// </summary>
    public static IReadOnlyDictionary<string, FailureAttributionArtifact> LoadAllFromDirectory(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (!Directory.Exists(directory))
            return new Dictionary<string, FailureAttributionArtifact>();

        var result = new Dictionary<string, FailureAttributionArtifact>(StringComparer.Ordinal);
        foreach (var file in Directory.GetFiles(directory, "*.failure-attribution.json", SearchOption.TopDirectoryOnly))
        {
            var stem = Path.GetFileNameWithoutExtension(file);
            // Strip the ".failure-attribution" suffix if double-extension form is used.
            var key = stem.EndsWith(".failure-attribution", StringComparison.Ordinal)
                ? stem[..^".failure-attribution".Length]
                : stem;
            result[key] = LoadFromFile(file);
        }
        return result;
    }
}
