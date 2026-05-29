// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;

namespace Vais.Agents;

/// <summary>
/// Declarative specification for an agent. The canonical shape for feeding an
/// <see cref="IAgentLifecycleManager.CreateAsync"/> call or for storing in an
/// <see cref="IAgentRegistry"/>; deliberately minimal — the intersection of what
/// every surveyed agent runtime requires (AWS Bedrock AgentCore, Dapr Agents,
/// OpenAI Assistants, Temporal, Knative).
/// </summary>
/// <remarks>
/// <para>
/// <b>v0.6 expansion.</b> The record ships with the original v0.4 positional
/// parameters unchanged (<see cref="Id"/>, <see cref="Version"/>, <see cref="Handler"/>,
/// <see cref="Protocols"/>, <see cref="Tools"/>, <see cref="Memory"/>,
/// <see cref="Identity"/>, <see cref="Autoscaling"/>, <see cref="Description"/>,
/// <see cref="Labels"/>) plus a set of new init-only properties covering the
/// library layer (<see cref="Model"/>, <see cref="SystemPrompt"/>, <see cref="McpServers"/>,
/// <see cref="Guardrails"/>, <see cref="Handoffs"/>, <see cref="Budget"/>,
/// <see cref="ContextProviders"/>, <see cref="OutputSchema"/>), the reasoning
/// layer (<see cref="AgentMode"/>, <see cref="Reasoning"/> — contract-only in v0.6),
/// and the control-plane overlay (<see cref="Observability"/>, <see cref="Annotations"/>).
/// Everything new is optional; consumers relying on the v0.4 ctor compile unchanged.
/// </para>
/// </remarks>
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
    IReadOnlyDictionary<string, string>? Labels = null)
{
    /// <summary>LLM model binding for declarative agents. Null when <see cref="Handler"/> carries custom behaviour.
    /// Set via object-initializer syntax: <c>new AgentManifest(...) { Model = new ModelSpec(...) }</c>.</summary>
    public ModelSpec? Model { get; init; }

    /// <summary>System prompt — inline text, template reference, or file reference. Exactly one shape set when non-null.</summary>
    public SystemPromptSpec? SystemPrompt { get; init; }

    /// <summary>Model Context Protocol server bindings. Each server contributes tools to the agent at activation time.</summary>
    public IReadOnlyList<McpServerRef>? McpServers { get; init; }

    /// <summary>Agent2Agent (A2A) remote-agent bindings. Referenced by <c>ToolRef.Source = "a2a:&lt;name&gt;"</c>. v0.17 Pillar B.</summary>
    [System.Text.Json.Serialization.JsonPropertyName("a2aRemoteAgents")]
    public IReadOnlyList<A2ARemoteAgentRef>? A2ARemoteAgents { get; init; }

    /// <summary>Local (same-runtime) agent bindings. Referenced by <c>ToolRef.Source = "agent:&lt;name&gt;"</c>. v0.18 — closes P7 agent-as-tool.</summary>
    public IReadOnlyList<LocalAgentRef>? LocalAgents { get; init; }

    /// <summary>Three-layer guardrail bindings (input / output / tool) — projected onto the equivalent <c>StatefulAgentOptions</c> fields at runtime.</summary>
    public GuardrailsSpec? Guardrails { get; init; }

    /// <summary>Declarative handoff targets — other agents this manifest can delegate to by id.</summary>
    public IReadOnlyList<HandoffRef>? Handoffs { get; init; }

    /// <summary>Per-run budget caps — projected onto <see cref="RunBudget"/> at runtime.</summary>
    public RunBudget? Budget { get; init; }

    /// <summary>Context providers bound to this agent — resolved against the host DI keyspace.</summary>
    public IReadOnlyList<ContextProviderRef>? ContextProviders { get; init; }

    /// <summary>Structured-output shape for the final assistant turn. Inline JSON Schema.</summary>
    public JsonElement? OutputSchema { get; init; }

    /// <summary>Execution-loop flavour — <see cref="Vais.Agents.AgentMode.ToolCalling"/> by default. Non-default values are contract-only in v0.6.</summary>
    public AgentMode AgentMode { get; init; } = AgentMode.ToolCalling;

    /// <summary>Schema-Guided Reasoning configuration. Contract-only in v0.6 — engine treats as <see cref="Vais.Agents.AgentMode.ToolCalling"/>.</summary>
    public ReasoningSpec? Reasoning { get; init; }

    /// <summary>
    /// Code-mode binding. When <see cref="CodeModeSpec.Enabled"/> is <c>true</c>, the agent is
    /// presented a single <c>run_code</c> affordance plus a generated JS API over its tools and
    /// executes LLM-authored scripts in a sandboxed runtime instead of per-tool JSON calls.
    /// Null = classic tool-calling (backwards compatible).
    /// </summary>
    public CodeModeSpec? CodeMode { get; init; }

    /// <summary>Observability overlays — Langfuse project, sampling, custom tags.</summary>
    public ObservabilitySpec? Observability { get; init; }

    /// <summary>Free-form annotations — operator-visible metadata not indexed by the registry. Parallel to K8s annotations.</summary>
    public IReadOnlyDictionary<string, string>? Annotations { get; init; }

    /// <summary>
    /// Optional reference to a deployed <see cref="LlmGatewayConfigManifest"/> by id.
    /// When set, the translator builds a per-agent <c>LlmGatewayPipeline</c> from the
    /// referenced config at grain activation, replacing (not appending to) the DI-global
    /// chain entirely.
    /// Null = DI-global chain applies unchanged (backwards compatible).
    /// </summary>
    public string? LlmGatewayRef { get; init; }

    /// <summary>
    /// Optional reference to a deployed <see cref="McpGatewayConfigManifest"/> by id.
    /// When set, a per-agent <c>ToolGatewayMiddleware</c> chain is built from the referenced
    /// config at grain activation, replacing the DI-global chain entirely.
    /// Null = DI-global chain applies unchanged.
    /// </summary>
    public string? McpGatewayRef { get; init; }
}

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
public sealed record MemoryRef(string Provider, string? ConnectionName = null)
{
    /// <summary>Memory scope — <c>"session"</c> (per-conversation) or <c>"agent"</c> (shared across sessions). Null = runtime default.</summary>
    public string? Scope { get; init; }

    /// <summary>Optional history-reducer name — resolved against the host DI keyspace.</summary>
    public string? HistoryReducer { get; init; }
}

/// <summary>Identity / auth configuration for the agent.</summary>
/// <param name="InboundAuth">Inbound-auth scheme reference (e.g., OAuth issuer, mTLS profile name).</param>
/// <param name="OutboundCredentials">Legacy single-credential reference. Kept for v0.4 back-compat; prefer <see cref="Credentials"/> for multi-credential manifests.</param>
public sealed record IdentityRef(string? InboundAuth = null, string? OutboundCredentials = null)
{
    /// <summary>
    /// List of outbound credentials the agent's tools / adapters can look up by name
    /// at invocation time. Introduced in v0.6 — supersedes the single-valued
    /// <see cref="OutboundCredentials"/> string when multiple credentials are needed.
    /// </summary>
    public IReadOnlyList<OutboundCredentialRef>? Credentials { get; init; }

    /// <summary>Optional required-claims map for inbound JWT validation (e.g. <c>scope: "agent:invoke"</c>).</summary>
    public IReadOnlyDictionary<string, string>? InboundClaims { get; init; }
}

/// <summary>Replica / concurrency autoscaling hints.</summary>
/// <param name="MinReplicas">Minimum replicas. 0 means scale-to-zero is allowed.</param>
/// <param name="MaxReplicas">Maximum replicas. Null means unbounded (runtime-default).</param>
/// <param name="Target">Free-form target metric — e.g., "concurrent-requests", "cpu:70%". Consumer-defined.</param>
public sealed record AutoscalingSpec(int MinReplicas = 0, int? MaxReplicas = null, string? Target = null)
{
    /// <summary>Optional numeric target value paired with <see cref="Target"/> — e.g. <c>Target: "cpu", TargetValue: 0.7</c>.</summary>
    public double? TargetValue { get; init; }

    /// <summary>Deactivate idle replicas after this duration. Null = runtime default.</summary>
    public TimeSpan? IdleTtl { get; init; }
}
