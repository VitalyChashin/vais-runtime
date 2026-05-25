// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Collections.Concurrent;
using System.Text.Json;
using Json.Schema;
using Microsoft.Extensions.DependencyInjection;
using Vais.Agents.Control.Manifests;

namespace Vais.Agents.Control.Mcp.Server;

/// <summary>
/// Dry-run manifest validator for the <c>vais.validate</c> tool (ND-7).
/// Performs JSON-Schema validation + cross-ref integrity checks; never mutates registry state.
/// </summary>
internal static class ManifestValidator
{
    private static readonly ConcurrentDictionary<string, JsonSchema?> SchemaCache = new(StringComparer.Ordinal);

    internal static async ValueTask<(bool Ok, List<string> Errors, List<string> Suggestions)> ValidateAsync(
        string manifestJson,
        IServiceProvider sp,
        CancellationToken ct)
    {
        var errors = new List<string>();
        var suggestions = new List<string>();

        JsonDocument doc;
        try { doc = JsonDocument.Parse(manifestJson); }
        catch (JsonException ex) { return (false, [$"Invalid JSON: {ex.Message}"], []); }

        using (doc)
        {
            var root = doc.RootElement;

            // Envelope shape — must have kind, metadata.id, spec
            if (!root.TryGetProperty("kind", out var kindEl)) errors.Add("Missing required envelope key: 'kind'.");
            if (!root.TryGetProperty("metadata", out var metaEl)) errors.Add("Missing required envelope key: 'metadata'.");
            else if (!metaEl.TryGetProperty("id", out _)) errors.Add("Missing required metadata field: 'id'.");
            if (!root.TryGetProperty("spec", out _)) errors.Add("Missing required envelope key: 'spec'.");

            if (errors.Count > 0)
                return (false, errors, []);

            var kind = kindEl.GetString()!;
            var specEl = root.GetProperty("spec");

            // JSON Schema validation (skipped for schema-less kinds such as Extension)
            var schema = GetSchema(kind);
            if (schema is not null)
            {
                using var instanceDoc = JsonDocument.Parse(manifestJson);
                var evalResult = schema.Evaluate(
                    instanceDoc.RootElement,
                    new EvaluationOptions { OutputFormat = OutputFormat.List });
                if (!evalResult.IsValid && evalResult.Details is { } details)
                {
                    foreach (var detail in details)
                    {
                        if (detail.Errors is { Count: > 0 })
                            foreach (var (_, msg) in detail.Errors)
                                errors.Add($"Schema ({detail.InstanceLocation}): {msg}");
                    }
                }
            }

            // Cross-ref integrity — resolve every *Ref field against the live registries
            var catalog = sp.GetRequiredService<IOntologyCatalog>();
            if (catalog.TryGet(kind, out var entry))
            {
                foreach (var crossRef in entry.CrossRefs)
                {
                    foreach (var refValue in ResolveRefs(specEl, crossRef.FieldPath))
                    {
                        var found = await DesignRegistryRouter
                            .GetAsync(crossRef.TargetKind, refValue, null, sp, ct)
                            .ConfigureAwait(false);
                        if (found is null)
                        {
                            errors.Add(
                                $"Dangling reference: spec.{crossRef.FieldPath} = '{refValue}' " +
                                $"references {crossRef.TargetKind}/{refValue} which is not registered.");
                            suggestions.Add(
                                $"Run vais.list {crossRef.TargetKind} to see registered resources, " +
                                $"or apply the missing {crossRef.TargetKind} manifest first.");
                        }
                    }
                }
            }
        }

        return (errors.Count == 0, errors, suggestions);
    }

    private static JsonSchema? GetSchema(string kind) =>
        SchemaCache.GetOrAdd(kind, static k =>
        {
            using var stream = typeof(ManifestValidator).Assembly
                .GetManifestResourceStream($"Vais.Agents.Control.Mcp.Server.schemas.{k}.schema.json");
            if (stream is null) return null;
            using var reader = new StreamReader(stream);
            return JsonSchema.FromText(reader.ReadToEnd());
        });

    private static IEnumerable<string> ResolveRefs(JsonElement spec, string fieldPath)
    {
        if (fieldPath.Contains("[]"))
        {
            var bracketIdx = fieldPath.IndexOf("[]", StringComparison.Ordinal);
            var arrayField = fieldPath[..bracketIdx];
            var subField = fieldPath[(bracketIdx + 2)..].TrimStart('.');
            if (!spec.TryGetProperty(arrayField, out var arr) || arr.ValueKind != JsonValueKind.Array)
                yield break;
            foreach (var item in arr.EnumerateArray())
                if (item.TryGetProperty(subField, out var val) && val.ValueKind == JsonValueKind.String)
                    if (val.GetString() is { Length: > 0 } v) yield return v;
        }
        else
        {
            if (spec.TryGetProperty(fieldPath, out var val) && val.ValueKind == JsonValueKind.String)
                if (val.GetString() is { Length: > 0 } v) yield return v;
        }
    }
}
