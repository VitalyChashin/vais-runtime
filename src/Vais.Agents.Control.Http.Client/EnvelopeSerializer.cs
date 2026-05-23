// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json.Nodes;

namespace Vais.Agents.Control.Http;

/// <summary>
/// Wraps manifest records into the v0.6 envelope shape (<c>apiVersion</c> + <c>kind</c> +
/// <c>metadata</c> + <c>spec</c>) the server's loader expects on the wire. Every kind
/// delegates to the generic <see cref="EnvelopeCodec"/>; AgentGraph supplies a spec
/// shape-hook for its <c>state.schema</c> wrapping. Kept internal — consumers only see
/// typed methods on <see cref="IAgentControlPlaneClient"/>.
/// </summary>
internal static class EnvelopeSerializer
{
    public static string Serialize(AgentManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return EnvelopeCodec.Serialize(manifest, "Agent");
    }

    public static string Serialize(AgentGraphManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return EnvelopeCodec.Serialize(manifest, "AgentGraph", WrapGraphState);
    }

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
}
