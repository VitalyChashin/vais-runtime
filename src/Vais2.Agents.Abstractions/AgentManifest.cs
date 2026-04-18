// Copyright (c) 2026 VAIS2 Platform contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais2.Agents;

/// <summary>
/// Declarative specification for an agent. The canonical shape for feeding an
/// <see cref="IAgentLifecycleManager.CreateAsync"/> call or for storing in an
/// <see cref="IAgentRegistry"/>; deliberately minimal — the intersection of what
/// every surveyed agent runtime requires (AWS Bedrock AgentCore, Dapr Agents,
/// OpenAI Assistants, Temporal, Knative).
/// </summary>
/// <param name="Id">Stable identifier. Unique within the registry namespace / tenant scope.</param>
/// <param name="Version">Immutable version tag. Updates create a new version; old versions remain for in-flight runs.</param>
/// <param name="Handler">Code reference (class name / image ref) the runtime instantiates.</param>
/// <param name="Protocols">Protocol bindings the agent is exposed on — HTTP, A2A, MCP, custom.</param>
/// <param name="Tools">Tools available to the agent. Each entry names a tool and optionally points at a source.</param>
/// <param name="Memory">Memory backing — pluggable store provider + connection ref. Null for ephemeral.</param>
/// <param name="Identity">Inbound/outbound auth configuration. Null for unauthenticated scenarios (dev, single-tenant).</param>
/// <param name="Autoscaling">Replica caps + target concurrency. Null for "whatever the runtime defaults to".</param>
/// <param name="Description">Human-readable description for registries / UIs.</param>
/// <param name="Labels">Arbitrary key/value metadata for filtering + organizing in the registry.</param>
public sealed record AgentManifest(
    string Id,
    string Version,
    AgentHandlerRef Handler,
    IReadOnlyList<ProtocolBinding> Protocols,
    IReadOnlyList<ToolRef> Tools,
    MemoryRef? Memory = null,
    IdentityRef? Identity = null,
    AutoscalingSpec? Autoscaling = null,
    string? Description = null,
    IReadOnlyDictionary<string, string>? Labels = null);

/// <summary>Reference to the code or image that implements an agent's handler.</summary>
/// <param name="TypeName">Fully-qualified .NET type name for in-process, or OCI image reference for containerized runtimes.</param>
/// <param name="AssemblyName">Optional assembly name; null when <paramref name="TypeName"/> is already fully qualified or when the runtime resolves via a different mechanism.</param>
public sealed record AgentHandlerRef(string TypeName, string? AssemblyName = null);

/// <summary>Declaration of a protocol the agent is exposed on.</summary>
/// <param name="Kind">Protocol name — "Http", "A2A", "Mcp", "SignalR", etc. Consumer-defined; no enum to keep the shape open.</param>
/// <param name="Endpoint">Optional endpoint hint (URL, path, channel name). Null when the runtime chooses.</param>
public sealed record ProtocolBinding(string Kind, string? Endpoint = null);

/// <summary>Reference to a tool available to the agent.</summary>
/// <param name="Name">Tool name — matches <see cref="ITool.Name"/>.</param>
/// <param name="Source">Optional source identifier (e.g., MCP-server id, A2A remote name). Null when the tool is registered locally.</param>
public sealed record ToolRef(string Name, string? Source = null);

/// <summary>Memory backing configuration for the agent.</summary>
/// <param name="Provider">Provider name — e.g., "Redis", "Postgres", "VectorData". Consumer-defined.</param>
/// <param name="ConnectionName">Optional connection / named-instance ref resolved at runtime.</param>
public sealed record MemoryRef(string Provider, string? ConnectionName = null);

/// <summary>Identity / auth configuration for the agent.</summary>
/// <param name="InboundAuth">Inbound-auth scheme reference (e.g., OAuth issuer, mTLS profile name).</param>
/// <param name="OutboundCredentials">Outbound-credentials reference (e.g., key-vault path prefix).</param>
public sealed record IdentityRef(string? InboundAuth = null, string? OutboundCredentials = null);

/// <summary>Replica / concurrency autoscaling hints.</summary>
/// <param name="MinReplicas">Minimum replicas. 0 means scale-to-zero is allowed.</param>
/// <param name="MaxReplicas">Maximum replicas. Null means unbounded (runtime-default).</param>
/// <param name="Target">Free-form target metric — e.g., "concurrent-requests", "cpu:70%". Consumer-defined.</param>
public sealed record AutoscalingSpec(int MinReplicas = 0, int? MaxReplicas = null, string? Target = null);
