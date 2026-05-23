// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Vais.Agents.Control.Http;

/// <summary>
/// Generic record → v0.6 envelope (<c>apiVersion</c> + <c>kind</c> + <c>metadata</c> +
/// <c>spec</c>) codec. Serializes any manifest record with System.Text.Json, then
/// partitions its top-level properties into the well-known metadata block
/// (<c>id</c>, <c>version</c>, <c>description</c>, <c>labels</c>, <c>annotations</c>)
/// and the spec block (everything else).
/// </summary>
/// <remarks>
/// <para>
/// One mapping replaces per-kind hand-written serializers so apply output stays inverse
/// with the loader. Closed-hierarchy wire forms (predicate/effect) ride STJ converters
/// attached to the record types, so they emit automatically. Enums serialize as their
/// (PascalCase) member name; the loaders parse case-insensitively. <see cref="TimeSpan"/>
/// uses the constant (<c>c</c>) format the loaders' <c>TimeSpan.Parse</c> accepts.
/// </para>
/// <para>
/// Records that nest their payload under a single <c>Spec</c> property (ContainerPlugin,
/// Plugin, EvalSuite) are unwrapped so the envelope spec is the <c>Spec</c> contents, not
/// <c>spec: { spec: {…} }</c>.
/// </para>
/// <para>
/// Phase 3 / MS-1 (<c>plans/manifest-serialization-source-of-truth-phase3-2026-05-23.md</c>).
/// Covers the "flat-mapping" kinds: gateway configs, McpServer, ContainerPlugin, EvalSuite.
/// AgentGraph (<c>state.schema</c> wrapping) and Agent (enum/property-order + separate
/// loader) keep hand-written serializers until the codec grows per-kind shape hooks or
/// MS-3 codegen lands.
/// </para>
/// </remarks>
internal static class EnvelopeCodec
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

    /// <summary>
    /// Serialize <paramref name="manifest"/> to a v0.6 envelope JSON string. An optional
    /// <paramref name="specHook"/> rewrites the assembled spec block before wrapping — used
    /// by kinds whose wire shape isn't a flat projection of the record (e.g. AgentGraph's
    /// <c>state.schema</c> wrapping).
    /// </summary>
    public static string Serialize<T>(T manifest, string kind, Action<JsonObject>? specHook = null)
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

        specHook?.Invoke(spec);

        var envelope = new JsonObject
        {
            ["apiVersion"] = ApiVersion,
            ["kind"] = kind,
            ["metadata"] = metadata,
            ["spec"] = spec,
        };
        return envelope.ToJsonString(Options);
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
