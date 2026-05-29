// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;

namespace Vais.Agents.Control.Kubernetes;

/// <summary>
/// The <c>.spec</c> subresource of an <see cref="AgentEntity"/> custom
/// resource. Mirrors the field set of <see cref="AgentManifest"/> —
/// declarative specification for an agent that the operator hands to the
/// control plane's <c>IAgentLifecycleManager.CreateAsync</c> /
/// <c>UpdateAsync</c> verbs after resolving any <see cref="SecretRefs"/>.
/// </summary>
/// <remarks>
/// <para>
/// The operator's internal <c>AgentSpecProjector</c> maps this type
/// field-by-field onto <see cref="AgentManifest"/>, substituting resolved
/// secret values into the appropriate manifest sub-structures. Users
/// author YAML against this type; consumers never construct it directly.
/// </para>
/// <para>
/// <b>Required fields</b> (<see cref="AgentId"/>, <see cref="Version"/>,
/// <see cref="Handler"/>, <see cref="Protocols"/>, <see cref="Tools"/>)
/// match <see cref="AgentManifest"/>'s ctor-positional parameters. A CR
/// that omits any required field is rejected by the CRD's OpenAPI
/// validation before the operator sees it.
/// </para>
/// </remarks>
public sealed class AgentSpec
{
    /// <summary>Stable identifier — unique within the owning tenant. Matches <see cref="AgentManifest.Id"/>.</summary>
    public string AgentId { get; set; } = string.Empty;

    /// <summary>Immutable version tag. Bumping this triggers an <c>UpdateAsync</c> against the control plane. Matches <see cref="AgentManifest.Version"/>.</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>Code reference (class name or image ref) the runtime instantiates. Matches <see cref="AgentManifest.Handler"/>.</summary>
    public AgentHandlerRef Handler { get; set; } = new(string.Empty);

    /// <summary>Protocol bindings the agent is exposed on. Matches <see cref="AgentManifest.Protocols"/>.</summary>
    public IList<ProtocolBinding> Protocols { get; set; } = new List<ProtocolBinding>();

    /// <summary>Tool references available to the agent. Matches <see cref="AgentManifest.Tools"/>.</summary>
    public IList<ToolRef> Tools { get; set; } = new List<ToolRef>();

    /// <summary>Memory backing — pluggable store provider + connection ref. Null for ephemeral.</summary>
    public MemoryRef? Memory { get; set; }

    /// <summary>Inbound / outbound auth configuration. Null for unauthenticated scenarios.</summary>
    public IdentityRef? Identity { get; set; }

    /// <summary>Replica caps + target concurrency. Null = runtime defaults.</summary>
    public AutoscalingSpec? Autoscaling { get; set; }

    /// <summary>Human-readable description for registries / UIs.</summary>
    public string? Description { get; set; }

    /// <summary>Registry-level key/value metadata. Distinct from <c>metadata.labels</c>; the former is agent-scope, the latter is K8s-scope.</summary>
    public IDictionary<string, string>? Labels { get; set; }

    /// <summary>LLM model binding for declarative agents. Null when <see cref="Handler"/> carries custom behaviour.</summary>
    public ModelSpec? Model { get; set; }

    /// <summary>System prompt — inline text, template ref, or file ref. Exactly one shape set when non-null.</summary>
    public SystemPromptSpec? SystemPrompt { get; set; }

    /// <summary>Model Context Protocol server bindings contributing tools at activation.</summary>
    public IList<McpServerRef>? McpServers { get; set; }

    /// <summary>Three-layer guardrail bindings (input / output / tool).</summary>
    public GuardrailsSpec? Guardrails { get; set; }

    /// <summary>Declarative handoff targets — other agents this manifest can delegate to.</summary>
    public IList<HandoffRef>? Handoffs { get; set; }

    /// <summary>Per-run budget caps.</summary>
    public RunBudget? Budget { get; set; }

    /// <summary>Context providers bound to this agent.</summary>
    public IList<ContextProviderRef>? ContextProviders { get; set; }

    /// <summary>Structured-output shape for the final assistant turn — inline JSON schema. Preserved as arbitrary JSON via <c>x-kubernetes-preserve-unknown-fields</c>.</summary>
    public JsonElement? OutputSchema { get; set; }

    /// <summary>Execution-loop flavour. Defaults to <see cref="AgentMode.ToolCalling"/>.</summary>
    public AgentMode AgentMode { get; set; } = AgentMode.ToolCalling;

    /// <summary>Schema-Guided Reasoning configuration. Contract-only in v0.6; engine treats as <see cref="AgentMode.ToolCalling"/>.</summary>
    public ReasoningSpec? Reasoning { get; set; }

    /// <summary>Code-mode binding. When enabled, the agent runs LLM-authored scripts in a sandboxed runtime. Null = classic tool-calling.</summary>
    public CodeModeSpec? CodeMode { get; set; }

    /// <summary>Observability overlays — Langfuse project, sampling, custom tags.</summary>
    public ObservabilitySpec? Observability { get; set; }

    /// <summary>Free-form annotations — operator-visible metadata not indexed by the registry. Distinct from <c>metadata.annotations</c>.</summary>
    public IDictionary<string, string>? Annotations { get; set; }

    /// <summary>
    /// Kubernetes-native secret references resolved by the operator before
    /// the projected <see cref="AgentManifest"/> reaches the control plane.
    /// Keys are logical names the manifest references (e.g.
    /// <c>OPENAI_API_KEY</c>); values point at a specific
    /// <c>Secret.data</c> key in the CR's namespace.
    /// </summary>
    public IDictionary<string, SecretKeyReference>? SecretRefs { get; set; }

    /// <summary>
    /// When <c>true</c>, deletion of the CR removes the
    /// <c>vais.io/agent-deactivate</c> finalizer without calling
    /// <c>EvictAsync</c> on the runtime. Agent state in the runtime
    /// persists for rebuild from a different source. When <c>false</c>
    /// (default), CR deletion triggers runtime eviction.
    /// </summary>
    public bool PreserveOnDelete { get; set; }
}
