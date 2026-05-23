// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using System.Text.Json.Nodes;

namespace Vais.Agents.Control.Http;

/// <summary>
/// Generic record → v0.6 envelope (<c>apiVersion</c> + <c>kind</c> + <c>metadata</c> +
/// <c>spec</c>) codec. Serializes any manifest record with System.Text.Json, then
/// partitions its top-level properties into the well-known metadata block
/// (<c>id</c>, <c>version</c>, <c>description</c>, <c>labels</c>, <c>annotations</c>)
/// and the spec block (everything else).
/// </summary>
/// <remarks>
/// One mapping for every kind — replaces the per-kind hand-written serializers so that
/// apply output and the loader stay inverse without bespoke code. Closed-hierarchy wire
/// forms (predicate/effect/reducer) ride STJ converters on the record types, so they are
/// emitted automatically here. Phase 3 / MS-1 (see
/// <c>plans/manifest-serialization-source-of-truth-phase3-2026-05-23.md</c>) — currently
/// used by the gateway-config serialize paths; remaining kinds migrate incrementally.
/// </remarks>
internal static class EnvelopeCodec
{
    private const string ApiVersion = "vais.agents/v1";

    private static readonly HashSet<string> MetadataKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "id", "version", "description", "labels", "annotations",
    };

    /// <summary>Serialize <paramref name="manifest"/> to a v0.6 envelope JSON string.</summary>
    public static string Serialize<T>(T manifest, string kind, JsonSerializerOptions options)
        where T : notnull
    {
        var flat = JsonSerializer.SerializeToNode(manifest, options)!.AsObject();
        var metadata = new JsonObject();
        var spec = new JsonObject();
        foreach (var property in flat)
        {
            var target = MetadataKeys.Contains(property.Key) ? metadata : spec;
            target[property.Key] = property.Value?.DeepClone();
        }

        var envelope = new JsonObject
        {
            ["apiVersion"] = ApiVersion,
            ["kind"] = kind,
            ["metadata"] = metadata,
            ["spec"] = spec,
        };
        return envelope.ToJsonString(options);
    }
}
