// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using System.Text.Json.Nodes;

namespace Vais.Agents.Control.Manifests;

/// <summary>
/// Wraps an <see cref="AgentGraphManifest"/> into the v0.6-style envelope shape
/// (<c>apiVersion</c> + <c>kind</c> + <c>metadata</c> + <c>spec</c>) that
/// <see cref="JsonAgentGraphManifestLoader"/> consumes on the wire. Parallel to
/// the <c>EnvelopeSerializer</c> in <c>Vais.Agents.Control.Http.Client</c> (kept
/// separate per the v0.7/v0.8 convention — server/client don't cross-reference).
/// </summary>
public static class AgentGraphManifestEnvelope
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Serialise <paramref name="manifest"/> to a v0.6 envelope JSON string.</summary>
    public static string Serialize(AgentGraphManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var metadata = new JsonObject
        {
            ["id"] = manifest.Id,
            ["version"] = manifest.Version,
        };
        if (manifest.Description is not null) metadata["description"] = manifest.Description;
        if (manifest.Labels is { Count: > 0 } labels)
        {
            metadata["labels"] = ToJsonObject(labels);
        }
        if (manifest.Annotations is { Count: > 0 } annotations)
        {
            metadata["annotations"] = ToJsonObject(annotations);
        }

        var spec = new JsonObject
        {
            ["entry"] = manifest.Entry,
            ["nodes"] = SerializeNodes(manifest.Nodes),
            ["edges"] = SerializeEdges(manifest.Edges),
        };

        if (manifest.StateSchema is JsonElement schema)
        {
            var stateObj = new JsonObject { ["schema"] = JsonNode.Parse(schema.GetRawText()) };
            spec["state"] = stateObj;
        }
        if (manifest.MaxSteps is int maxSteps)
        {
            spec["maxSteps"] = maxSteps;
        }
        if (manifest.StateReducers is { Count: > 0 } reducers)
        {
            spec["stateReducers"] = SerializeStateReducers(reducers);
        }

        var envelope = new JsonObject
        {
            ["apiVersion"] = "vais.agents/v1",
            ["kind"] = "AgentGraph",
            ["metadata"] = metadata,
            ["spec"] = spec,
        };
        return envelope.ToJsonString(JsonOptions);
    }

    private static JsonArray SerializeNodes(IReadOnlyList<GraphNode> nodes)
    {
        var arr = new JsonArray();
        foreach (var node in nodes)
        {
            var obj = new JsonObject
            {
                ["id"] = node.Id,
                ["kind"] = node.Kind,
            };
            if (node.Ref is { } aRef)
            {
                var refObj = new JsonObject { ["id"] = aRef.Id };
                if (aRef.Version is not null) refObj["version"] = aRef.Version;
                if (aRef.RuntimeUrl is not null) refObj["runtimeUrl"] = aRef.RuntimeUrl;
                obj["ref"] = refObj;
            }
            if (node.HandlerRef is { } hRef)
            {
                obj["handlerRef"] = SerializeHandlerRef(hRef);
            }
            if (node.StateBindings is { } bindings)
            {
                var bObj = new JsonObject();
                if (bindings.Input is { Count: > 0 }) bObj["input"] = new JsonArray(bindings.Input.Select(s => (JsonNode?)s).ToArray());
                if (bindings.Output is { Count: > 0 }) bObj["output"] = new JsonArray(bindings.Output.Select(s => (JsonNode?)s).ToArray());
                if (bObj.Count > 0) obj["stateBindings"] = bObj;
            }
            if (node.InterruptReason is not null) obj["interruptReason"] = node.InterruptReason;
            if (node.RetryPolicy is { } rp)
            {
                obj["retryPolicy"] = new JsonObject
                {
                    ["maxAttempts"] = rp.MaxAttempts,
                    ["initialBackoffSeconds"] = rp.InitialBackoffSeconds,
                    ["backoffMultiplier"] = rp.BackoffMultiplier,
                    ["maxBackoffSeconds"] = rp.MaxBackoffSeconds,
                };
            }
            arr.Add(obj);
        }
        return arr;
    }

    private static JsonArray SerializeEdges(IReadOnlyList<GraphEdge> edges)
    {
        var arr = new JsonArray();
        foreach (var edge in edges)
        {
            var obj = new JsonObject
            {
                ["from"] = edge.From,
                ["to"] = edge.To,
            };
            if (edge.When is not null) obj["when"] = SerializePredicate(edge.When);
            if (edge.OnTraverse is not null) obj["onTraverse"] = SerializeEffect(edge.OnTraverse);
            if (edge.Concurrent) obj["concurrent"] = true;
            arr.Add(obj);
        }
        return arr;
    }

    private static JsonNode SerializePredicate(GraphEdgePredicate predicate)
    {
        return predicate switch
        {
            GraphEdgePredicate.Always => (JsonNode)"always",
            GraphEdgePredicate.Expression e => (JsonNode)e.Expr,
            GraphEdgePredicate.PropertyMatcher m => SerializeMatcher(m),
            GraphEdgePredicate.AllOf a => new JsonObject { ["allOf"] = new JsonArray(a.Predicates.Select(p => (JsonNode?)SerializePredicate(p)).ToArray()) },
            GraphEdgePredicate.AnyOf a => new JsonObject { ["anyOf"] = new JsonArray(a.Predicates.Select(p => (JsonNode?)SerializePredicate(p)).ToArray()) },
            GraphEdgePredicate.Not n => new JsonObject { ["not"] = SerializePredicate(n.Predicate) },
            GraphEdgePredicate.HandlerRef h => new JsonObject { ["handlerRef"] = SerializeHandlerRef(h.Handler) },
            _ => throw new NotSupportedException($"Unknown predicate subtype '{predicate.GetType().Name}'."),
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

    private static JsonObject SerializeEffect(GraphEdgeEffect effect)
    {
        return effect switch
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
            GraphEdgeEffect.HandlerRef h => new JsonObject { ["handlerRef"] = SerializeHandlerRef(h.Handler) },
            _ => throw new NotSupportedException($"Unknown effect subtype '{effect.GetType().Name}'."),
        };
    }

    private static JsonObject SerializeStateReducers(IReadOnlyDictionary<string, GraphStateReducer> reducers)
    {
        var obj = new JsonObject();
        foreach (var (key, reducer) in reducers)
        {
            obj[key] = SerializeReducer(reducer);
        }
        return obj;
    }

    private static JsonNode SerializeReducer(GraphStateReducer reducer)
    {
        return reducer switch
        {
            GraphStateReducer.LastWriteWins => (JsonNode)"lastWriteWins",
            GraphStateReducer.FirstWriteWins => (JsonNode)"firstWriteWins",
            GraphStateReducer.Append => (JsonNode)"append",
            GraphStateReducer.HandlerRef hr => new JsonObject { ["handlerRef"] = SerializeHandlerRef(hr.Handler) },
            _ => throw new NotSupportedException($"Unknown reducer subtype '{reducer.GetType().Name}'."),
        };
    }

    private static JsonObject SerializeHandlerRef(GraphHandlerRef handler)
    {
        var obj = new JsonObject { ["typeName"] = handler.TypeName };
        if (handler.AssemblyName is not null) obj["assemblyName"] = handler.AssemblyName;
        return obj;
    }

    private static JsonObject ToJsonObject(IReadOnlyDictionary<string, string> map)
    {
        var obj = new JsonObject();
        foreach (var kv in map) obj[kv.Key] = kv.Value;
        return obj;
    }
}
