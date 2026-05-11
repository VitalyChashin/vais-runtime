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
            if (edge.Concurrent) obj["concurrent"] = true;
            arr.Add(obj);
        }
        return arr;
    }

    public static string Serialize(LlmGatewayConfigManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var metadata = BuildGatewayMetadata(manifest.Id, manifest.Version, manifest.Description, manifest.Labels, manifest.Annotations);
        var spec = new JsonObject { ["middleware"] = SerializeMiddleware(manifest.Middleware) };
        if (manifest.RateLimit is { } rl)
        {
            var rlObj = new JsonObject();
            if (rl.RequestsPerMinute is int rpm) rlObj["requestsPerMinute"] = rpm;
            if (rl.TokensPerMinute is int tpm) rlObj["tokensPerMinute"] = tpm;
            spec["rateLimit"] = rlObj;
        }
        return WrapEnvelope("LlmGatewayConfig", metadata, spec).ToJsonString(JsonOptions);
    }

    public static string Serialize(McpGatewayConfigManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var metadata = BuildGatewayMetadata(manifest.Id, manifest.Version, manifest.Description, manifest.Labels, manifest.Annotations);
        var spec = new JsonObject { ["middleware"] = SerializeMiddleware(manifest.Middleware) };
        if (manifest.WorkspacePolicies is { Count: > 0 } wp)
            spec["workspacePolicies"] = JsonSerializer.SerializeToNode(wp, JsonOptions);
        return WrapEnvelope("McpGatewayConfig", metadata, spec).ToJsonString(JsonOptions);
    }

    public static string Serialize(McpServerManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var metadata = BuildGatewayMetadata(manifest.Id, manifest.Version, manifest.Description, manifest.Labels, manifest.Annotations);
        var spec = new JsonObject();
        if (manifest.Virtual) spec["virtual"] = true;
        AddIfSet(spec, "transport", manifest.Transport);
        AddIfSet(spec, "url", manifest.Url);
        AddIfSet(spec, "command", manifest.Command);
        if (manifest.Args is { Count: > 0 }) spec["args"] = JsonSerializer.SerializeToNode(manifest.Args, JsonOptions);
        if (manifest.Env is { Count: > 0 }) spec["env"] = JsonSerializer.SerializeToNode(manifest.Env, JsonOptions);
        AddIfSet(spec, "authRef", manifest.AuthRef);
        if (manifest.Tools is { Count: > 0 }) spec["tools"] = JsonSerializer.SerializeToNode(manifest.Tools, JsonOptions);
        if (manifest.Sources is { Count: > 0 }) spec["sources"] = JsonSerializer.SerializeToNode(manifest.Sources, JsonOptions);
        if (manifest.ToolProjection is { Count: > 0 }) spec["toolProjection"] = JsonSerializer.SerializeToNode(manifest.ToolProjection, JsonOptions);
        AddIfSet(spec, "mcpGatewayRef", manifest.McpGatewayRef);
        return WrapEnvelope("McpServer", metadata, spec).ToJsonString(JsonOptions);
    }

    public static string Serialize(ContainerPluginManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var metadata = BuildGatewayMetadata(manifest.Id, manifest.Version, manifest.Description, manifest.Labels, annotations: null);
        var s = manifest.Spec;
        var spec = new JsonObject { ["image"] = s.Image };
        if (s.Port != 8080) spec["port"] = s.Port;
        if (!string.Equals(s.Topology, "standalone", StringComparison.Ordinal)) spec["topology"] = s.Topology;
        if (s.StartupTimeoutSeconds != 30) spec["startupTimeoutSeconds"] = s.StartupTimeoutSeconds;
        if (s.InvokeTimeoutSeconds != 60) spec["invokeTimeoutSeconds"] = s.InvokeTimeoutSeconds;
        if (!string.Equals(s.ImagePullPolicy, "IfNotPresent", StringComparison.Ordinal)) spec["imagePullPolicy"] = s.ImagePullPolicy;
        if (s.Build is { } build) spec["build"] = JsonSerializer.SerializeToNode(build, JsonOptions);
        if (s.RetryPolicy is { } rp) spec["retryPolicy"] = JsonSerializer.SerializeToNode(rp, JsonOptions);
        if (s.Kubernetes is { } k8s) spec["kubernetes"] = JsonSerializer.SerializeToNode(k8s, JsonOptions);
        if (s.Secrets is { Count: > 0 }) spec["secrets"] = JsonSerializer.SerializeToNode(s.Secrets, JsonOptions);
        return WrapEnvelope("ContainerPlugin", metadata, spec).ToJsonString(JsonOptions);
    }

    private static JsonObject BuildGatewayMetadata(string id, string version, string? description,
        IReadOnlyDictionary<string, string>? labels, IReadOnlyDictionary<string, string>? annotations)
    {
        var metadata = new JsonObject { ["id"] = id, ["version"] = version };
        if (description is not null) metadata["description"] = description;
        if (labels is { Count: > 0 }) metadata["labels"] = ToJsonObject(labels);
        if (annotations is { Count: > 0 }) metadata["annotations"] = ToJsonObject(annotations);
        return metadata;
    }

    private static JsonObject WrapEnvelope(string kind, JsonObject metadata, JsonObject spec)
        => new() { ["apiVersion"] = "vais.agents/v1", ["kind"] = kind, ["metadata"] = metadata, ["spec"] = spec };

    private static JsonArray SerializeMiddleware(IReadOnlyList<GatewayMiddlewareSpec> middleware)
    {
        var arr = new JsonArray();
        foreach (var m in middleware)
        {
            var obj = new JsonObject { ["name"] = m.Name };
            if (m.Params is System.Text.Json.JsonElement p && p.ValueKind != System.Text.Json.JsonValueKind.Null)
                obj["params"] = JsonNode.Parse(p.GetRawText());
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
