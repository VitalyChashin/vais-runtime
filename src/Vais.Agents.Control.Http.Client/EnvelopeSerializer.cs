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
            if (node.Ref is { } r) { var ro = new JsonObject { ["id"] = r.Id }; if (r.Version is not null) ro["version"] = r.Version; obj["ref"] = ro; }
            if (node.HandlerRef is { } h) obj["handlerRef"] = new JsonObject { ["typeName"] = h.TypeName };
            if (node.StateBindings is { } b)
            {
                var bObj = new JsonObject();
                if (b.Input is { Count: > 0 }) bObj["input"] = new JsonArray(b.Input.Select(s => (JsonNode?)s).ToArray());
                if (b.Output is { Count: > 0 }) bObj["output"] = new JsonArray(b.Output.Select(s => (JsonNode?)s).ToArray());
                if (bObj.Count > 0) obj["stateBindings"] = bObj;
            }
            if (node.InterruptReason is not null) obj["interruptReason"] = node.InterruptReason;
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
            arr.Add(obj);
        }
        return arr;
    }

    private static JsonObject ToJsonObject(IReadOnlyDictionary<string, string> map)
    {
        var obj = new JsonObject();
        foreach (var kv in map) obj[kv.Key] = kv.Value;
        return obj;
    }
}
