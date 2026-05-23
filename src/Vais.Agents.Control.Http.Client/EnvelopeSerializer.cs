// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using System.Text.Json.Nodes;

namespace Vais.Agents.Control.Http;

/// <summary>
/// Wraps an <see cref="AgentManifest"/> into the v0.6 envelope shape
/// (<c>apiVersion</c> + <c>kind</c> + <c>metadata</c> + <c>spec</c>) the server's
/// manifest loader expects on the wire. Kept out of the public surface — consumers
/// only see typed methods on <see cref="IAgentControlPlaneClient"/>.
/// </summary>
internal static class EnvelopeSerializer
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Serialize(AgentManifest manifest)
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

        var spec = new JsonObject();
        AddIfSet(spec, "handler", manifest.Handler);
        if (manifest.Protocols is { Count: > 0 }) spec["protocols"] = JsonSerializer.SerializeToNode(manifest.Protocols, JsonOptions);
        if (manifest.Tools is { Count: > 0 }) spec["tools"] = JsonSerializer.SerializeToNode(manifest.Tools, JsonOptions);
        AddIfSet(spec, "memory", manifest.Memory);
        AddIfSet(spec, "identity", manifest.Identity);
        AddIfSet(spec, "autoscaling", manifest.Autoscaling);
        AddIfSet(spec, "model", manifest.Model);
        AddIfSet(spec, "systemPrompt", manifest.SystemPrompt);
        if (manifest.McpServers is { Count: > 0 }) spec["mcpServers"] = JsonSerializer.SerializeToNode(manifest.McpServers, JsonOptions);
        AddIfSet(spec, "guardrails", manifest.Guardrails);
        if (manifest.Handoffs is { Count: > 0 }) spec["handoffs"] = JsonSerializer.SerializeToNode(manifest.Handoffs, JsonOptions);
        AddIfSet(spec, "budget", manifest.Budget);
        if (manifest.ContextProviders is { Count: > 0 }) spec["contextProviders"] = JsonSerializer.SerializeToNode(manifest.ContextProviders, JsonOptions);
        if (manifest.OutputSchema is JsonElement os) spec["outputSchema"] = JsonNode.Parse(os.GetRawText());
        if (manifest.AgentMode != AgentMode.ToolCalling) spec["agentMode"] = manifest.AgentMode.ToString();
        AddIfSet(spec, "reasoning", manifest.Reasoning);
        AddIfSet(spec, "observability", manifest.Observability);
        AddIfSet(spec, "llmGatewayRef", manifest.LlmGatewayRef);
        AddIfSet(spec, "mcpGatewayRef", manifest.McpGatewayRef);
        if (manifest.LocalAgents is { Count: > 0 }) spec["localAgents"] = JsonSerializer.SerializeToNode(manifest.LocalAgents, JsonOptions);
        if (manifest.A2ARemoteAgents is { Count: > 0 }) spec["a2aRemoteAgents"] = JsonSerializer.SerializeToNode(manifest.A2ARemoteAgents, JsonOptions);

        var envelope = new JsonObject
        {
            ["apiVersion"] = "vais.agents/v1",
            ["kind"] = "Agent",
            ["metadata"] = metadata,
            ["spec"] = spec,
        };
        return envelope.ToJsonString(JsonOptions);
    }

    private static void AddIfSet<T>(JsonObject target, string key, T? value) where T : class
    {
        if (value is null) return;
        target[key] = JsonSerializer.SerializeToNode(value, JsonOptions);
    }

    public static string Serialize(AgentGraphManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var metadata = new JsonObject
        {
            ["id"] = manifest.Id,
            ["version"] = manifest.Version,
        };
        if (manifest.Description is not null) metadata["description"] = manifest.Description;
        if (manifest.Labels is { Count: > 0 } labels) metadata["labels"] = ToJsonObject(labels);
        if (manifest.Annotations is { Count: > 0 } annotations) metadata["annotations"] = ToJsonObject(annotations);

        var spec = new JsonObject
        {
            ["entry"] = manifest.Entry,
            ["nodes"] = SerializeGraphNodes(manifest.Nodes),
            ["edges"] = SerializeGraphEdges(manifest.Edges),
        };
        if (manifest.StateSchema is System.Text.Json.JsonElement schema)
            spec["state"] = new JsonObject { ["schema"] = JsonNode.Parse(schema.GetRawText()) };
        if (manifest.MaxSteps is int maxSteps) spec["maxSteps"] = maxSteps;
        if (manifest.StateReducers is { Count: > 0 } reducers) spec["stateReducers"] = SerializeGraphStateReducers(reducers);

        var envelope = new JsonObject
        {
            ["apiVersion"] = "vais.agents/v1",
            ["kind"] = "AgentGraph",
            ["metadata"] = metadata,
            ["spec"] = spec,
        };
        return envelope.ToJsonString(JsonOptions);
    }

    private static JsonArray SerializeGraphNodes(IReadOnlyList<Vais.Agents.GraphNode> nodes)
    {
        var arr = new JsonArray();
        foreach (var node in nodes)
        {
            var obj = new JsonObject { ["id"] = node.Id, ["kind"] = node.Kind };
            if (node.Ref is { } r)
            {
                var ro = new JsonObject { ["id"] = r.Id };
                if (r.Version is not null) ro["version"] = r.Version;
                if (r.RuntimeUrl is not null) ro["runtimeUrl"] = r.RuntimeUrl;
                if (r.A2AUrl is not null) ro["a2aUrl"] = r.A2AUrl;
                obj["ref"] = ro;
            }
            if (node.HandlerRef is { } h) obj["handlerRef"] = SerializeGraphHandlerRef(h);
            if (node.StateBindings is { } b)
            {
                var bObj = new JsonObject();
                if (b.Input is { Count: > 0 }) bObj["input"] = new JsonArray(b.Input.Select(s => (JsonNode?)s).ToArray());
                if (b.Output is { Count: > 0 }) bObj["output"] = new JsonArray(b.Output.Select(s => (JsonNode?)s).ToArray());
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

    private static JsonArray SerializeGraphEdges(IReadOnlyList<Vais.Agents.GraphEdge> edges)
    {
        var arr = new JsonArray();
        foreach (var edge in edges)
        {
            var obj = new JsonObject { ["from"] = edge.From, ["to"] = edge.To };
            if (edge.When is not null) obj["when"] = SerializeGraphPredicate(edge.When);
            if (edge.OnTraverse is not null) obj["onTraverse"] = SerializeGraphEffect(edge.OnTraverse);
            if (edge.Concurrent) obj["concurrent"] = true;
            arr.Add(obj);
        }
        return arr;
    }

    private static JsonNode SerializeGraphPredicate(Vais.Agents.GraphEdgePredicate predicate)
    {
        return predicate switch
        {
            Vais.Agents.GraphEdgePredicate.Always => (JsonNode)"always",
            Vais.Agents.GraphEdgePredicate.Expression e => (JsonNode)e.Expr,
            Vais.Agents.GraphEdgePredicate.PropertyMatcher m => SerializeGraphMatcher(m),
            Vais.Agents.GraphEdgePredicate.AllOf a => new JsonObject { ["allOf"] = new JsonArray(a.Predicates.Select(p => (JsonNode?)SerializeGraphPredicate(p)).ToArray()) },
            Vais.Agents.GraphEdgePredicate.AnyOf a => new JsonObject { ["anyOf"] = new JsonArray(a.Predicates.Select(p => (JsonNode?)SerializeGraphPredicate(p)).ToArray()) },
            Vais.Agents.GraphEdgePredicate.Not n => new JsonObject { ["not"] = SerializeGraphPredicate(n.Predicate) },
            Vais.Agents.GraphEdgePredicate.HandlerRef h => new JsonObject { ["handlerRef"] = SerializeGraphHandlerRef(h.Handler) },
            _ => throw new NotSupportedException($"Unknown predicate subtype '{predicate.GetType().Name}'."),
        };
    }

    private static JsonObject SerializeGraphMatcher(Vais.Agents.GraphEdgePredicate.PropertyMatcher matcher)
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

    private static JsonObject SerializeGraphEffect(Vais.Agents.GraphEdgeEffect effect)
    {
        return effect switch
        {
            Vais.Agents.GraphEdgeEffect.Set s => new JsonObject
            {
                ["set"] = new JsonObject
                {
                    ["property"] = s.Property,
                    ["value"] = JsonNode.Parse(s.Value.GetRawText()),
                },
            },
            Vais.Agents.GraphEdgeEffect.Increment i => new JsonObject
            {
                ["increment"] = new JsonObject
                {
                    ["property"] = i.Property,
                    ["by"] = i.By,
                },
            },
            Vais.Agents.GraphEdgeEffect.Append a => new JsonObject
            {
                ["append"] = new JsonObject
                {
                    ["property"] = a.Property,
                    ["value"] = JsonNode.Parse(a.Value.GetRawText()),
                },
            },
            Vais.Agents.GraphEdgeEffect.HandlerRef h => new JsonObject { ["handlerRef"] = SerializeGraphHandlerRef(h.Handler) },
            _ => throw new NotSupportedException($"Unknown effect subtype '{effect.GetType().Name}'."),
        };
    }

    private static JsonObject SerializeGraphHandlerRef(Vais.Agents.GraphHandlerRef handler)
    {
        var obj = new JsonObject { ["typeName"] = handler.TypeName };
        if (handler.AssemblyName is not null) obj["assemblyName"] = handler.AssemblyName;
        return obj;
    }

    private static JsonObject SerializeGraphStateReducers(IReadOnlyDictionary<string, Vais.Agents.GraphStateReducer> reducers)
    {
        var obj = new JsonObject();
        foreach (var (key, reducer) in reducers) obj[key] = SerializeGraphReducer(reducer);
        return obj;
    }

    private static JsonNode SerializeGraphReducer(Vais.Agents.GraphStateReducer reducer)
    {
        return reducer switch
        {
            Vais.Agents.GraphStateReducer.LastWriteWins => (JsonNode)"lastWriteWins",
            Vais.Agents.GraphStateReducer.FirstWriteWins => (JsonNode)"firstWriteWins",
            Vais.Agents.GraphStateReducer.Append => (JsonNode)"append",
            Vais.Agents.GraphStateReducer.HandlerRef hr => new JsonObject { ["handlerRef"] = SerializeGraphHandlerRef(hr.Handler) },
            _ => throw new NotSupportedException($"Unknown reducer subtype '{reducer.GetType().Name}'."),
        };
    }

    // MS-1 (Phase 3): "flat-mapping" kinds serialize through the generic EnvelopeCodec.
    // AgentManifest + AgentGraphManifest keep hand-written serializers (enum/property-order
    // and the spec.state.schema wrapping the naive codec can't express yet).
    public static string Serialize(LlmGatewayConfigManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return EnvelopeCodec.Serialize(manifest, "LlmGatewayConfig");
    }

    public static string Serialize(McpGatewayConfigManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return EnvelopeCodec.Serialize(manifest, "McpGatewayConfig");
    }

    public static string Serialize(McpServerManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return EnvelopeCodec.Serialize(manifest, "McpServer");
    }

    public static string Serialize(ContainerPluginManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return EnvelopeCodec.Serialize(manifest, "ContainerPlugin");
    }

    public static string Serialize(EvalSuiteManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return EnvelopeCodec.Serialize(manifest, "EvalSuite");
    }

    private static JsonObject ToJsonObject(IReadOnlyDictionary<string, string> map)
    {
        var obj = new JsonObject();
        foreach (var kv in map) obj[kv.Key] = kv.Value;
        return obj;
    }
}
