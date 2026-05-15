// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Vais.Agents;

/// <summary>
/// System.Text.Json polymorphic converter for the <see cref="GraphEdgePredicate"/>
/// closed hierarchy. Mirrors the wire shape that <c>JsonAgentGraphManifestLoader</c>
/// accepts so default-STJ round-trip is symmetric with the loader. Attached to
/// <see cref="GraphEdgePredicate"/> via <see cref="JsonConverterAttribute"/> so no
/// per-call options registration is required.
/// </summary>
public sealed class GraphEdgePredicateJsonConverter : JsonConverter<GraphEdgePredicate>
{
    /// <inheritdoc />
    public override GraphEdgePredicate? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;
        var node = JsonNode.Parse(ref reader);
        return ParseNode(node);
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, GraphEdgePredicate value, JsonSerializerOptions options)
    {
        SerializeNode(value).WriteTo(writer);
    }

    private static GraphEdgePredicate? ParseNode(JsonNode? node)
    {
        if (node is null) return null;
        if (node is JsonValue v && v.TryGetValue<string>(out var s))
        {
            if (string.Equals(s, "always", StringComparison.Ordinal))
                return new GraphEdgePredicate.Always();
            if (s.StartsWith('='))
                return new GraphEdgePredicate.Expression(s);
            throw new JsonException($"GraphEdgePredicate string must be 'always' or start with '=', got '{s}'.");
        }
        if (node is not JsonObject obj)
        {
            throw new JsonException($"GraphEdgePredicate must be a JSON object or string, got {node.GetType().Name}.");
        }

        if (obj.TryGetPropertyValue("allOf", out var allEl) && allEl is JsonArray allArr)
        {
            var items = new List<GraphEdgePredicate>();
            foreach (var child in allArr)
            {
                var parsed = ParseNode(child);
                if (parsed is not null) items.Add(parsed);
            }
            return new GraphEdgePredicate.AllOf(items);
        }
        if (obj.TryGetPropertyValue("anyOf", out var anyEl) && anyEl is JsonArray anyArr)
        {
            var items = new List<GraphEdgePredicate>();
            foreach (var child in anyArr)
            {
                var parsed = ParseNode(child);
                if (parsed is not null) items.Add(parsed);
            }
            return new GraphEdgePredicate.AnyOf(items);
        }
        if (obj.TryGetPropertyValue("not", out var notEl) && notEl is not null)
        {
            var inner = ParseNode(notEl) ?? throw new JsonException("GraphEdgePredicate.Not requires inner predicate.");
            return new GraphEdgePredicate.Not(inner);
        }
        if (obj.TryGetPropertyValue("handlerRef", out var hrEl) && hrEl is JsonObject hrObj)
        {
            var typeName = hrObj["typeName"]?.GetValue<string>() ?? throw new JsonException("handlerRef.typeName required.");
            var asm = hrObj["assemblyName"]?.GetValue<string>();
            return new GraphEdgePredicate.HandlerRef(new GraphHandlerRef(typeName, asm));
        }
        if (obj.TryGetPropertyValue("property", out var propEl) && propEl is not null
            && obj.TryGetPropertyValue("operator", out var opEl) && opEl is not null)
        {
            var property = propEl.GetValue<string>();
            var opStr = opEl.GetValue<string>();
            if (!Enum.TryParse<GraphPredicateOperator>(opStr, ignoreCase: true, out var op))
            {
                throw new JsonException($"Unknown GraphPredicateOperator '{opStr}'.");
            }
            JsonElement? value = null;
            if (obj.TryGetPropertyValue("value", out var valEl) && valEl is not null)
            {
                value = JsonDocument.Parse(valEl.ToJsonString()).RootElement.Clone();
            }
            return new GraphEdgePredicate.PropertyMatcher(property, op, value);
        }

        throw new JsonException("GraphEdgePredicate object must contain one of: allOf, anyOf, not, handlerRef, or {property, operator}.");
    }

    private static JsonNode SerializeNode(GraphEdgePredicate predicate)
    {
        return predicate switch
        {
            GraphEdgePredicate.Always => (JsonNode)"always",
            GraphEdgePredicate.Expression e => (JsonNode)e.Expr,
            GraphEdgePredicate.PropertyMatcher m => SerializeMatcher(m),
            GraphEdgePredicate.AllOf a => new JsonObject { ["allOf"] = new JsonArray(a.Predicates.Select(p => (JsonNode?)SerializeNode(p)).ToArray()) },
            GraphEdgePredicate.AnyOf a => new JsonObject { ["anyOf"] = new JsonArray(a.Predicates.Select(p => (JsonNode?)SerializeNode(p)).ToArray()) },
            GraphEdgePredicate.Not n => new JsonObject { ["not"] = SerializeNode(n.Predicate) },
            GraphEdgePredicate.HandlerRef h => new JsonObject { ["handlerRef"] = SerializeHandlerRef(h.Handler) },
            _ => throw new NotSupportedException($"Unknown GraphEdgePredicate subtype '{predicate.GetType().Name}'."),
        };
    }

    private static JsonObject SerializeMatcher(GraphEdgePredicate.PropertyMatcher matcher)
    {
        var obj = new JsonObject
        {
            ["property"] = matcher.Property,
            ["operator"] = matcher.Operator.ToString(),
        };
        if (matcher.Value is JsonElement value)
        {
            obj["value"] = JsonNode.Parse(value.GetRawText());
        }
        return obj;
    }

    private static JsonObject SerializeHandlerRef(GraphHandlerRef handler)
    {
        var obj = new JsonObject { ["typeName"] = handler.TypeName };
        if (handler.AssemblyName is not null) obj["assemblyName"] = handler.AssemblyName;
        return obj;
    }
}

/// <summary>
/// System.Text.Json polymorphic converter for the <see cref="GraphEdgeEffect"/>
/// closed hierarchy. Mirrors the wire shape (<c>set</c> / <c>increment</c> /
/// <c>append</c> / <c>handlerRef</c>) used by <c>JsonAgentGraphManifestLoader</c>.
/// </summary>
public sealed class GraphEdgeEffectJsonConverter : JsonConverter<GraphEdgeEffect>
{
    /// <inheritdoc />
    public override GraphEdgeEffect? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;
        var node = JsonNode.Parse(ref reader);
        if (node is not JsonObject obj)
        {
            throw new JsonException("GraphEdgeEffect must be a JSON object.");
        }

        if (obj.TryGetPropertyValue("set", out var setEl) && setEl is JsonObject setObj)
        {
            var prop = setObj["property"]?.GetValue<string>() ?? throw new JsonException("set.property required.");
            var valueNode = setObj["value"] ?? throw new JsonException("set.value required.");
            var valueEl = JsonDocument.Parse(valueNode.ToJsonString()).RootElement.Clone();
            return new GraphEdgeEffect.Set(prop, valueEl);
        }
        if (obj.TryGetPropertyValue("increment", out var incEl) && incEl is JsonObject incObj)
        {
            var prop = incObj["property"]?.GetValue<string>() ?? throw new JsonException("increment.property required.");
            var by = incObj.TryGetPropertyValue("by", out var byNode) && byNode is not null
                ? byNode.GetValue<int>()
                : 1;
            return new GraphEdgeEffect.Increment(prop, by);
        }
        if (obj.TryGetPropertyValue("append", out var appEl) && appEl is JsonObject appObj)
        {
            var prop = appObj["property"]?.GetValue<string>() ?? throw new JsonException("append.property required.");
            var valueNode = appObj["value"] ?? throw new JsonException("append.value required.");
            var valueEl = JsonDocument.Parse(valueNode.ToJsonString()).RootElement.Clone();
            return new GraphEdgeEffect.Append(prop, valueEl);
        }
        if (obj.TryGetPropertyValue("handlerRef", out var hrEl) && hrEl is JsonObject hrObj)
        {
            var typeName = hrObj["typeName"]?.GetValue<string>() ?? throw new JsonException("handlerRef.typeName required.");
            var asm = hrObj["assemblyName"]?.GetValue<string>();
            return new GraphEdgeEffect.HandlerRef(new GraphHandlerRef(typeName, asm));
        }
        throw new JsonException("GraphEdgeEffect object must contain one of: set, increment, append, handlerRef.");
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, GraphEdgeEffect value, JsonSerializerOptions options)
    {
        var node = value switch
        {
            GraphEdgeEffect.Set s => new JsonObject
            {
                ["set"] = new JsonObject
                {
                    ["property"] = s.Property,
                    ["value"] = JsonNode.Parse(s.Value.GetRawText()),
                },
            },
            GraphEdgeEffect.Increment i => new JsonObject
            {
                ["increment"] = new JsonObject
                {
                    ["property"] = i.Property,
                    ["by"] = i.By,
                },
            },
            GraphEdgeEffect.Append a => new JsonObject
            {
                ["append"] = new JsonObject
                {
                    ["property"] = a.Property,
                    ["value"] = JsonNode.Parse(a.Value.GetRawText()),
                },
            },
            GraphEdgeEffect.HandlerRef h => new JsonObject
            {
                ["handlerRef"] = new JsonObject { ["typeName"] = h.Handler.TypeName, ["assemblyName"] = h.Handler.AssemblyName },
            },
            _ => throw new NotSupportedException($"Unknown GraphEdgeEffect subtype '{value.GetType().Name}'."),
        };
        node.WriteTo(writer);
    }
}
