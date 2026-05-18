// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;

namespace Vais.Agents.Runtime.Host;

/// <summary>
/// Parses the <c>pool:</c> array inside a manifest-declared <c>Fallback</c>
/// gateway middleware's <c>params</c> block into a sequence of <see cref="ModelSpec"/>
/// records. Each entry is a full model spec — operators can vary not just
/// provider/id/key but also endpoint, sampling, and response format per pool entry.
/// </summary>
internal static class FallbackPoolManifestParser
{
    /// <summary>
    /// Enumerate pool entries from the <c>params</c> JSON block. Returns an empty
    /// sequence when <paramref name="paramsEl"/> is null or has no <c>pool</c> array.
    /// </summary>
    public static IEnumerable<ModelSpec> ParsePool(JsonElement? paramsEl)
    {
        if (paramsEl is not { } p || !p.TryGetProperty("pool", out var poolEl) || poolEl.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (var entry in poolEl.EnumerateArray())
            yield return ParseEntry(entry);
    }

    /// <summary>
    /// Parse a single pool entry into a <see cref="ModelSpec"/>. <c>provider</c> and
    /// <c>id</c> are required; all other ModelSpec fields are optional and forwarded
    /// when present so the downstream model-provider factory sees the same spec it
    /// would have received from a top-level <c>spec.model</c> manifest block.
    /// </summary>
    public static ModelSpec ParseEntry(JsonElement entry)
    {
        var provider = entry.GetProperty("provider").GetString()!;
        var id = entry.GetProperty("id").GetString()!;

        return new ModelSpec(
            Provider: provider,
            Id: id,
            ApiKeyRef: ReadString(entry, "apiKeyRef"),
            BaseUrlRef: ReadString(entry, "baseUrlRef"),
            Temperature: ReadDouble(entry, "temperature"),
            TopP: ReadDouble(entry, "topP"),
            MaxTokens: ReadInt(entry, "maxTokens"),
            ResponseFormat: ReadString(entry, "responseFormat"));
    }

    private static string? ReadString(JsonElement entry, string name)
        => entry.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;

    private static double? ReadDouble(JsonElement entry, string name)
        => entry.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number
            ? el.GetDouble()
            : null;

    private static int? ReadInt(JsonElement entry, string name)
        => entry.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number
            ? el.GetInt32()
            : null;
}
