// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Vais.Agents.Control.Manifests;

/// <summary>Cross-reference edge from a manifest field to a target kind.</summary>
/// <param name="FieldPath">JSON field path (e.g. <c>llmGatewayRef</c> or <c>sources[].ref</c>).</param>
/// <param name="TargetKind">The manifest kind the field resolves to (e.g. <c>LlmGatewayConfig</c>).</param>
/// <param name="Cardinality"><c>one</c> or <c>many</c>.</param>
public sealed record CrossRefEdge(string FieldPath, string TargetKind, string Cardinality);

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
/// <para>
/// Optional per-field <c>description</c>s are supplied by the caller (keyed by XML-doc
/// member id <c>P:Namespace.Type.Property</c>), loaded from the assembly's XML doc file — so
/// this stays free of any runtime doc-file dependency.
/// </para>
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
    /// <param name="recordType">The manifest record type.</param>
    /// <param name="kind">The envelope <c>kind</c> value.</param>
    /// <param name="descriptions">Optional XML-doc member-id → description map for per-field <c>description</c>s.</param>
    public static string GenerateEnvelopeSchema(Type recordType, string kind, IReadOnlyDictionary<string, string>? descriptions = null)
    {
        ArgumentNullException.ThrowIfNull(recordType);
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);

        var spec = BuildSpecSchema(recordType, descriptions);
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

    private static JsonObject BuildSpecSchema(Type recordType, IReadOnlyDictionary<string, string>? descriptions)
    {
        var specProps = recordType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => !MetadataKeys.Contains(WireName(p)))
            .ToList();

        // Records that nest their payload under a single `Spec` property (ContainerPlugin,
        // EvalSuite) — the envelope spec is that type's contents.
        if (specProps is [var only] && string.Equals(WireName(only), "spec", StringComparison.OrdinalIgnoreCase)
            && IsConcreteRecord(Underlying(only.PropertyType)))
        {
            return RecordSchema(Underlying(only.PropertyType), new HashSet<Type>(), descriptions);
        }

        return ObjectSchema(specProps, new HashSet<Type>(), descriptions);
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

    private static JsonObject RecordSchema(Type recordType, HashSet<Type> visited, IReadOnlyDictionary<string, string>? descriptions)
        => ObjectSchema(recordType.GetProperties(BindingFlags.Public | BindingFlags.Instance), visited, descriptions);

    private static JsonObject ObjectSchema(IEnumerable<PropertyInfo> props, HashSet<Type> visited, IReadOnlyDictionary<string, string>? descriptions)
    {
        var properties = new JsonObject();
        foreach (var prop in props)
        {
            var schema = TypeSchema(prop.PropertyType, visited, descriptions);
            if (descriptions is not null && schema is JsonObject obj
                && descriptions.TryGetValue(MemberId(prop), out var description))
            {
                obj["description"] = description;
            }
            properties[WireName(prop)] = schema;
        }
        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["additionalProperties"] = false,
        };
    }

    private static JsonNode TypeSchema(Type type, HashSet<Type> visited, IReadOnlyDictionary<string, string>? descriptions)
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
            return new JsonObject { ["type"] = "object", ["additionalProperties"] = TypeSchema(valueType, visited, descriptions) };
        if (ListElementType(t) is { } elemType)
            return new JsonObject { ["type"] = "array", ["items"] = TypeSchema(elemType, visited, descriptions) };

        if (IsConcreteRecord(t))
        {
            if (!visited.Add(t)) return new JsonObject(); // cycle guard → any
            var schema = RecordSchema(t, visited, descriptions);
            visited.Remove(t);
            return schema;
        }

        // Abstract closed hierarchies (predicate/effect/reducer) + anything else → any.
        return new JsonObject();
    }

    private static JsonObject StringMapSchema()
        => new() { ["type"] = "object", ["additionalProperties"] = new JsonObject { ["type"] = "string" } };

    private static string MemberId(PropertyInfo prop) => $"P:{prop.DeclaringType!.FullName}.{prop.Name}";

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

    // ── Base-ontology generator (ND-1 / ND-2) ─────────────────────────────────

    /// <summary>
    /// Generates the <c>contracts/ontology/base-ontology.json</c> artifact — per-kind concepts,
    /// constraints (required fields), field types + descriptions, and typed cross-ref edges —
    /// from the manifest records in <paramref name="kinds"/>.
    /// Regenerate by running the guard test with <c>VAIS_UPDATE_ONTOLOGY=1</c>.
    /// </summary>
    /// <param name="kinds">Ordered list of (kind-name, record-type) pairs to include.</param>
    /// <param name="descriptions">Optional XML-doc member-id → description map (same as for <see cref="GenerateEnvelopeSchema"/>).</param>
    /// <param name="ontologyVersion">Version string stamped into the artifact (defaults to <c>"unknown"</c>).</param>
    public static string GenerateBaseOntology(
        IReadOnlyList<(string kind, Type recordType)> kinds,
        IReadOnlyDictionary<string, string>? descriptions = null,
        string? ontologyVersion = null)
    {
        ArgumentNullException.ThrowIfNull(kinds);

        var kindObjects = new JsonObject();
        foreach (var (kind, recordType) in kinds)
            kindObjects[kind] = BuildOntologyKindEntry(kind, recordType, descriptions);

        var root = new JsonObject
        {
            ["ontologyVersion"] = ontologyVersion ?? "unknown",
            ["apiVersion"] = ApiVersion,
            ["kinds"] = kindObjects,
        };
        return root.ToJsonString(WriteOptions);
    }

    private static JsonObject BuildOntologyKindEntry(
        string kind,
        Type recordType,
        IReadOnlyDictionary<string, string>? descriptions)
    {
        var (specType, specProps) = GetOntologySpecTypeAndProps(recordType);

        var fields = new JsonObject();
        foreach (var prop in specProps)
            fields[WireName(prop)] = BuildOntologyFieldNode(prop, descriptions);

        // Mirror the AgentGraph state-hook so the ontology agrees with the wire shape.
        if (kind == "AgentGraph" && fields.ContainsKey("stateSchema"))
        {
            fields.Remove("stateSchema");
            fields["state"] = new JsonObject
            {
                ["type"] = "object",
                ["description"] = "Optional JSON Schema for graph state (nested as spec.state.schema on the wire).",
            };
        }

        var required = new JsonArray(
            GetOntologyRequiredNames(specType).Select(n => (JsonNode?)n).ToArray());

        var crossRefs = new JsonArray();
        if (KindCrossRefs.TryGetValue(kind, out var edges))
        {
            foreach (var edge in edges)
                crossRefs.Add(new JsonObject
                {
                    ["field"] = edge.FieldPath,
                    ["targetKind"] = edge.TargetKind,
                    ["cardinality"] = edge.Cardinality,
                });
        }

        var entry = new JsonObject
        {
            ["constraints"] = new JsonObject { ["required"] = required },
            ["fields"] = fields,
            ["crossRefs"] = crossRefs,
        };

        // Kind-level description from XML doc (keyed as T:Namespace.Type)
        if (descriptions?.TryGetValue($"T:{recordType.FullName}", out var kindDesc) == true && kindDesc.Length > 0)
            entry["description"] = kindDesc;

        return entry;
    }

    private static JsonObject BuildOntologyFieldNode(
        PropertyInfo prop,
        IReadOnlyDictionary<string, string>? descriptions)
    {
        var t = Underlying(prop.PropertyType);
        var node = new JsonObject { ["type"] = OntologyTypeName(t) };

        if (t.IsEnum)
            node["enum"] = new JsonArray(Enum.GetNames(t).Select(n => (JsonNode?)n).ToArray());

        if (descriptions?.TryGetValue(MemberId(prop), out var desc) == true && desc.Length > 0)
            node["description"] = desc;

        return node;
    }

    private static string OntologyTypeName(Type t)
    {
        if (t == typeof(string)) return "string";
        if (t == typeof(bool)) return "boolean";
        if (t == typeof(byte) || t == typeof(short) || t == typeof(int) || t == typeof(long)
            || t == typeof(uint) || t == typeof(ulong) || t == typeof(ushort)) return "integer";
        if (t == typeof(float) || t == typeof(double) || t == typeof(decimal)) return "number";
        if (t == typeof(Uri)) return "string";
        if (t == typeof(TimeSpan)) return "string";
        if (t == typeof(JsonElement)) return "any";
        if (t.IsEnum) return "string";
        if (DictionaryValueType(t) is not null) return "object";
        if (ListElementType(t) is not null) return "array";
        if (IsConcreteRecord(t)) return "object";
        return "any";
    }

    private static (Type specType, IReadOnlyList<PropertyInfo> props) GetOntologySpecTypeAndProps(Type recordType)
    {
        var specProps = recordType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => !MetadataKeys.Contains(WireName(p)))
            .ToList();

        // Mirror the nested-Spec unwrap from BuildSpecSchema (ContainerPlugin, EvalSuite).
        if (specProps is [var only] && string.Equals(WireName(only), "spec", StringComparison.OrdinalIgnoreCase)
            && IsConcreteRecord(Underlying(only.PropertyType)))
        {
            var nestedType = Underlying(only.PropertyType);
            return (nestedType, nestedType.GetProperties(BindingFlags.Public | BindingFlags.Instance).ToList());
        }

        return (recordType, specProps);
    }

    private static IEnumerable<string> GetOntologyRequiredNames(Type specType)
    {
        // Primary constructor = the one with the most parameters (C# record primary ctor).
        var ctor = specType.GetConstructors()
            .OrderByDescending(c => c.GetParameters().Length)
            .FirstOrDefault();
        if (ctor is null) yield break;

        foreach (var p in ctor.GetParameters())
        {
            if (p.HasDefaultValue) continue;
            var name = p.Name!;
            var wireName = char.ToLowerInvariant(name[0]) + name[1..];
            if (!MetadataKeys.Contains(wireName))
                yield return wireName;
        }
    }

    // Statically declared cross-reference edges (ND-2).
    // Key = source kind; value = list of (fieldPath, targetKind, cardinality).
    // fieldPath uses dot-notation; [] denotes array traversal.
    private static readonly Dictionary<string, IReadOnlyList<CrossRefEdge>> KindCrossRefs = new()
    {
        ["Agent"] =
        [
            new("llmGatewayRef",  "LlmGatewayConfig", "one"),
            new("mcpGatewayRef",  "McpGatewayConfig",  "one"),
        ],
        ["McpServer"] =
        [
            new("mcpGatewayRef",  "McpGatewayConfig",  "one"),
            new("sources[].ref",  "McpServer",          "many"),  // virtual server upstream sources
        ],
        ["AgentGraph"] =
        [
            new("nodes[].ref.id", "Agent",              "many"),  // Agent-kind graph nodes
        ],
    };
}
