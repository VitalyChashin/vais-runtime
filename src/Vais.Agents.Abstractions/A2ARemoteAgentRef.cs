// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents;

/// <summary>
/// Declarative reference to a remote agent exposed over the Agent2Agent (A2A)
/// protocol. When a <see cref="ToolRef.Source"/> matches <c>"a2a:&lt;name&gt;"</c>
/// the runtime wraps the remote agent as an <c>ITool</c> via
/// <c>A2ARemoteAgentTool</c> so the local agent can delegate to it. Ships with
/// v0.17 Pillar B — complements <see cref="McpServerRef"/> for non-MCP remote
/// capability providers.
/// </summary>
/// <param name="Name">
/// Stable name for this A2A binding — referenced from
/// <c>ToolRef.Source = "a2a:&lt;Name&gt;"</c>. Unique within a manifest.
/// </param>
/// <param name="Url">Absolute URL of the remote agent endpoint (the A2A server's base URL).</param>
/// <param name="AuthRef">Optional <c>secret://</c> URI resolving to a bearer token for outbound Authorization.</param>
/// <param name="Metadata">
/// Optional key/value pairs attached to the outbound A2A calls — e.g. trace
/// tags, tenant hints. Consumed by <c>A2ARemoteAgentTool</c> when wiring the
/// HTTP layer.
/// </param>
public sealed record A2ARemoteAgentRef(
    string Name,
    Uri Url,
    string? AuthRef = null,
    IReadOnlyDictionary<string, string>? Metadata = null);
