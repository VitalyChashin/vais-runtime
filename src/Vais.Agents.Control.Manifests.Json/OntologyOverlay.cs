// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;

namespace Vais.Agents.Control.Manifests;

/// <summary>
/// Deployment-local overlay that augments the generated base ontology with
/// capability/risk tags, description overrides, author-role annotations, and
/// authoring recipes — keyed by kind or field path.
/// The overlay mechanism is OSS; the overlay <em>content</em> (org risk tags,
/// author policies) stays deployment-local and is never checked into
/// <c>agentic/</c>.
/// Merge is additive and deterministic: overlay values win over base; missing
/// overlay = base only.
/// </summary>
public sealed record OntologyOverlay
{
    /// <summary>Per-kind overlays keyed by manifest kind name (e.g. <c>ContainerPlugin</c>).</summary>
    public IReadOnlyDictionary<string, KindOverlay>? Kinds { get; init; }

    /// <summary>Authoring recipes — ordered sequences of kinds/actions that encode safe deployment patterns.</summary>
    public IReadOnlyList<RecipeEntry>? Recipes { get; init; }

    /// <summary>Returns a non-null overlay for <paramref name="kind"/>, or an empty one.</summary>
    public KindOverlay ForKind(string kind)
        => Kinds?.TryGetValue(kind, out var o) == true ? o : KindOverlay.Empty;

    /// <summary>An overlay with no entries (base-only mode).</summary>
    public static readonly OntologyOverlay Empty = new();
}

/// <summary>Overlay for a single manifest kind.</summary>
public sealed record KindOverlay
{
    /// <summary>Description override. Null = use base description.</summary>
    public string? Description { get; init; }

    /// <summary>
    /// Capability / risk tags (e.g. <c>risk:RunsCode</c>, <c>role:author</c>).
    /// Merged with any tags that already exist in the base; duplicates removed.
    /// </summary>
    public IReadOnlyList<string>? Tags { get; init; }

    /// <summary>Per-field overlays keyed by wire field name (e.g. <c>image</c>).</summary>
    public IReadOnlyDictionary<string, FieldOverlay>? Fields { get; init; }

    /// <summary>
    /// Manual concept text for kinds that have no base-schema entry (e.g. <c>Extension</c>).
    /// When non-null, this text is the primary documentation for the kind in <c>vais.describe</c>.
    /// </summary>
    public string? ManualConcept { get; init; }

    internal static readonly KindOverlay Empty = new();
}

/// <summary>Overlay for a single field within a kind.</summary>
public sealed record FieldOverlay
{
    /// <summary>Description override for this field. Null = use base description.</summary>
    public string? Description { get; init; }

    /// <summary>Additional tags for this field.</summary>
    public IReadOnlyList<string>? Tags { get; init; }
}

/// <summary>An authoring recipe — an ordered sequence of manifest operations encoding a safe deployment pattern.</summary>
public sealed record RecipeEntry
{
    /// <summary>Machine-readable recipe name (e.g. <c>gateway-before-agent</c>).</summary>
    public required string Name { get; init; }

    /// <summary>Human-readable description of the pattern and why it exists.</summary>
    public string? Description { get; init; }

    /// <summary>Ordered steps. Each step names a kind and an optional action hint.</summary>
    public IReadOnlyList<RecipeStep> Steps { get; init; } = [];
}

/// <summary>One step in a <see cref="RecipeEntry"/>.</summary>
public sealed record RecipeStep
{
    /// <summary>Manifest kind to act on (e.g. <c>LlmGatewayConfig</c>).</summary>
    public required string Kind { get; init; }

    /// <summary>Recommended action (<c>apply</c> | <c>validate</c> | <c>describe</c>). Informational.</summary>
    public string? Action { get; init; }

    /// <summary>Optional explanation for this step.</summary>
    public string? Note { get; init; }
}

/// <summary>
/// Loads an <see cref="OntologyOverlay"/> from a JSON file or JSON string.
/// Deployment-local overlays are not checked into <c>agentic/</c>; callers
/// supply the path (e.g. from a runtime config option).
/// A missing or null file is valid — the result is <see cref="OntologyOverlay.Empty"/>.
/// </summary>
public static class OntologyOverlayLoader
{
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// Loads an overlay from <paramref name="path"/>. Returns <see cref="OntologyOverlay.Empty"/> when
    /// <paramref name="path"/> is null, empty, or the file does not exist.
    /// </summary>
    public static OntologyOverlay LoadFromFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return OntologyOverlay.Empty;
        var json = File.ReadAllText(path);
        return LoadFromJson(json);
    }

    /// <summary>Parses an overlay from a JSON string. Returns <see cref="OntologyOverlay.Empty"/> for null / empty input.</summary>
    public static OntologyOverlay LoadFromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return OntologyOverlay.Empty;
        return JsonSerializer.Deserialize<OntologyOverlay>(json, ReadOptions) ?? OntologyOverlay.Empty;
    }
}
