// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Vais.Agents.Control.Manifests;

/// <summary>
/// Generic record ↔ v0.6 envelope (<c>apiVersion</c> + <c>kind</c> + <c>metadata</c> +
/// <c>spec</c>) codec. Serializes any manifest record with System.Text.Json, then
/// partitions its top-level properties into the well-known metadata block
/// (<c>id</c>, <c>version</c>, <c>description</c>, <c>labels</c>, <c>annotations</c>)
/// and the spec block (everything else).
/// </summary>
/// <remarks>
/// <para>
/// Shared by the client serialize path (<c>EnvelopeSerializer</c>) and the server-side
/// <see cref="AgentGraphManifestEnvelope"/>; the parse-side loaders move onto it next
/// (MS-1c, <c>plans/manifest-serialization-source-of-truth-phase3-2026-05-23.md</c>).
/// One mapping replaces the per-kind hand-written serializers so apply output stays
/// inverse with the loader.
/// </para>
/// <para>
/// Closed-hierarchy wire forms (predicate/effect) ride STJ converters attached to the
/// record types; <c>GraphStateReducer</c> uses a codec-scoped converter. Enums serialize
/// as their member name (loaders parse case-insensitively); <see cref="TimeSpan"/> uses
/// the constant (<c>c</c>) format. Records nesting their payload under a single <c>Spec</c>
/// property (ContainerPlugin, Plugin, EvalSuite) are unwrapped. Kinds whose wire shape
/// isn't a flat projection register a per-kind <see cref="SpecHooks">spec hook</see>
/// (AgentGraph rewraps <c>stateSchema</c> → <c>state.schema</c>).
/// </para>
/// </remarks>
public static class EnvelopeCodec
{
    private const string ApiVersion = "vais.agents/v1";

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters =
        {
            new JsonStringEnumConverter(),
            new TimeSpanConstantConverter(),
            new GraphStateReducerJsonConverter(),
        },
    };

    private static readonly HashSet<string> MetadataKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "id", "version", "description", "labels", "annotations",
    };

    // Per-kind spec shaping for kinds whose wire form isn't a flat projection of the record.
    private static readonly IReadOnlyDictionary<string, Action<JsonObject>> SpecHooks =
        new Dictionary<string, Action<JsonObject>>(StringComparer.Ordinal)
        {
            ["AgentGraph"] = WrapGraphState,
        };

    /// <summary>Serialize <paramref name="manifest"/> to a v0.6 envelope JSON string.</summary>
    public static string Serialize<T>(T manifest, string kind)
        where T : notnull
    {
        var flat = JsonSerializer.SerializeToNode(manifest, Options)!.AsObject();
        var metadata = new JsonObject();
        var spec = new JsonObject();
        foreach (var property in flat)
        {
            if (MetadataKeys.Contains(property.Key))
            {
                metadata[property.Key] = property.Value?.DeepClone();
            }
            else if (string.Equals(property.Key, "spec", StringComparison.OrdinalIgnoreCase)
                     && property.Value is JsonObject nested)
            {
                foreach (var inner in nested)
                    spec[inner.Key] = inner.Value?.DeepClone();
            }
            else
            {
                spec[property.Key] = property.Value?.DeepClone();
            }
        }

        if (SpecHooks.TryGetValue(kind, out var hook))
            hook(spec);

        var envelope = new JsonObject
        {
            ["apiVersion"] = ApiVersion,
            ["kind"] = kind,
            ["metadata"] = metadata,
            ["spec"] = spec,
        };
        return envelope.ToJsonString(Options);
    }

    // AgentGraph carries its JSON Schema flat as StateSchema, but the wire form nests it
    // under spec.state.schema. Rewrap after the generic flatten so the loader sees it.
    private static void WrapGraphState(JsonObject spec)
    {
        if (spec["stateSchema"] is { } schema)
        {
            var clone = schema.DeepClone();
            spec.Remove("stateSchema");
            spec["state"] = new JsonObject { ["schema"] = clone };
        }
    }

    // The loaders parse durations via TimeSpan.Parse; emit the constant ("c") format
    // ("00:30:00") rather than relying on STJ's built-in TimeSpan representation.
    private sealed class TimeSpanConstantConverter : JsonConverter<TimeSpan>
    {
        public override TimeSpan Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => TimeSpan.Parse(reader.GetString()!, CultureInfo.InvariantCulture);

        public override void Write(Utf8JsonWriter writer, TimeSpan value, JsonSerializerOptions options)
            => writer.WriteStringValue(value.ToString(null, CultureInfo.InvariantCulture));
    }
}
