// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Vais.Agents.Control.Manifests;

/// <summary>
/// Generates a JSON Schema (draft 2020-12) for a manifest kind's v0.6 envelope
/// (<c>apiVersion</c> + <c>kind</c> + <c>metadata</c> + <c>spec</c>) by reflecting the
/// record type. Phase 3 / MS-3-B — the records are the single source; this is the
/// schema half of the generated outputs (docs + example YAML follow).
/// </summary>
/// <remarks>
/// Deliberately mirrors the wire shape <see cref="EnvelopeCodec"/> emits, reusing the
/// same signals the codec relies on so the two can't drift: <see cref="JsonPropertyNameAttribute"/>
/// (covers <c>a2aUrl</c>/<c>a2aRemoteAgents</c>), enum member names, the metadata-key set,
/// and the nested-<c>Spec</c> unwrap. The one rule with no type-level equivalent — AgentGraph's
/// <c>stateSchema → spec.state.schema</c> hook — is reproduced here and guarded by tests.
/// Closed hierarchies (predicate/effect/reducer) are permissive (any) in v1.
/// </remarks>
public static class ManifestJsonSchemaGenerator
{
    private const string ApiVersion = "vais.agents/v1";

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    private static readonly HashSet<string> MetadataKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "id", "version", "description", "labels", "annotations",
    };

    /// <summary>Generate the indented JSON Schema string for <paramref name="recordType"/> as envelope <paramref name="kind"/>.</summary>
    public static string GenerateEnvelopeSchema(Type recordType, string kind)
    {
        ArgumentNullException.ThrowIfNull(recordType);
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);

        var spec = BuildSpecSchema(recordType);
        if (kind == "AgentGraph")
            ApplyGraphStateHook(spec);

        var envelope = new JsonObject
        {
            ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
            ["title"] = $"vais.agents {kind} manifest",
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["apiVersion"] = new JsonObject { ["const"] = ApiVersion },
                ["kind"] = new JsonObject { ["const"] = kind },
                ["metadata"] = MetadataSchema(),
                ["spec"] = spec,
            },
            ["required"] = new JsonArray("apiVersion", "kind", "metadata", "spec"),
            ["additionalProperties"] = false,
        };
        return envelope.ToJsonString(WriteOptions);
    }

    private static JsonObject MetadataSchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["id"] = new JsonObject { ["type"] = "string" },
            ["version"] = new JsonObject { ["type"] = "string" },
            ["description"] = new JsonObject { ["type"] = "string" },
            ["labels"] = StringMapSchema(),
            ["annotations"] = StringMapSchema(),
        },
        ["required"] = new JsonArray("id", "version"),
        ["additionalProperties"] = false,
    };

    private static JsonObject BuildSpecSchema(Type recordType)
    {
        var specProps = recordType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => !MetadataKeys.Contains(WireName(p)))
            .ToList();

        // Records that nest their payload under a single `Spec` property (ContainerPlugin,
        // EvalSuite) — the envelope spec is that type's contents.
        if (specProps is [var only] && string.Equals(WireName(only), "spec", StringComparison.OrdinalIgnoreCase)
            && IsConcreteRecord(Underlying(only.PropertyType)))
        {
            return RecordSchema(Underlying(only.PropertyType), new HashSet<Type>());
        }

        return ObjectSchema(specProps, new HashSet<Type>());
    }

    // AgentGraph stores its JSON Schema flat as StateSchema, but the wire nests it under
    // spec.state.schema (EnvelopeCodec's per-kind hook). Mirror that here.
    private static void ApplyGraphStateHook(JsonObject spec)
    {
        if (spec["properties"] is JsonObject props && props.Remove("stateSchema"))
        {
            props["state"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject { ["schema"] = new JsonObject() },
                ["additionalProperties"] = false,
            };
        }
    }

    private static JsonObject RecordSchema(Type recordType, HashSet<Type> visited)
        => ObjectSchema(recordType.GetProperties(BindingFlags.Public | BindingFlags.Instance), visited);

    private static JsonObject ObjectSchema(IEnumerable<PropertyInfo> props, HashSet<Type> visited)
    {
        var properties = new JsonObject();
        foreach (var prop in props)
            properties[WireName(prop)] = TypeSchema(prop.PropertyType, visited);
        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["additionalProperties"] = false,
        };
    }

    private static JsonNode TypeSchema(Type type, HashSet<Type> visited)
    {
        var t = Underlying(type);

        if (t == typeof(string)) return new JsonObject { ["type"] = "string" };
        if (t == typeof(bool)) return new JsonObject { ["type"] = "boolean" };
        if (t == typeof(byte) || t == typeof(short) || t == typeof(int) || t == typeof(long)
            || t == typeof(uint) || t == typeof(ulong) || t == typeof(ushort))
            return new JsonObject { ["type"] = "integer" };
        if (t == typeof(float) || t == typeof(double) || t == typeof(decimal))
            return new JsonObject { ["type"] = "number" };
        if (t == typeof(Uri)) return new JsonObject { ["type"] = "string", ["format"] = "uri" };
        if (t == typeof(TimeSpan)) return new JsonObject { ["type"] = "string" };
        if (t == typeof(JsonElement)) return new JsonObject(); // any
        if (t.IsEnum)
            return new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(Enum.GetNames(t).Select(n => (JsonNode?)n).ToArray()) };

        if (DictionaryValueType(t) is { } valueType)
            return new JsonObject { ["type"] = "object", ["additionalProperties"] = TypeSchema(valueType, visited) };
        if (ListElementType(t) is { } elemType)
            return new JsonObject { ["type"] = "array", ["items"] = TypeSchema(elemType, visited) };

        if (IsConcreteRecord(t))
        {
            if (!visited.Add(t)) return new JsonObject(); // cycle guard → any
            var schema = RecordSchema(t, visited);
            visited.Remove(t);
            return schema;
        }

        // Abstract closed hierarchies (predicate/effect/reducer) + anything else → any.
        return new JsonObject();
    }

    private static JsonObject StringMapSchema()
        => new() { ["type"] = "object", ["additionalProperties"] = new JsonObject { ["type"] = "string" } };

    private static string WireName(PropertyInfo prop)
    {
        var attr = prop.GetCustomAttribute<JsonPropertyNameAttribute>();
        if (attr is not null) return attr.Name;
        var name = prop.Name;
        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    private static Type Underlying(Type t) => Nullable.GetUnderlyingType(t) ?? t;

    private static bool IsConcreteRecord(Type t)
        => t.IsClass && !t.IsAbstract && t.Namespace == "Vais.Agents" && t != typeof(string)
           && t.GetMethod("<Clone>$", BindingFlags.Public | BindingFlags.Instance) is not null;

    private static Type? ListElementType(Type t)
    {
        if (t == typeof(string)) return null;
        if (t.IsArray) return t.GetElementType();
        foreach (var i in new[] { t }.Concat(t.GetInterfaces()))
        {
            if (i.IsGenericType)
            {
                var def = i.GetGenericTypeDefinition();
                if (def == typeof(IReadOnlyList<>) || def == typeof(IList<>) || def == typeof(IEnumerable<>))
                    return i.GetGenericArguments()[0];
            }
        }
        return null;
    }

    private static Type? DictionaryValueType(Type t)
    {
        foreach (var i in new[] { t }.Concat(t.GetInterfaces()))
        {
            if (i.IsGenericType)
            {
                var def = i.GetGenericTypeDefinition();
                if (def == typeof(IReadOnlyDictionary<,>) || def == typeof(IDictionary<,>))
                    return i.GetGenericArguments()[1];
            }
        }
        return null;
    }
}
