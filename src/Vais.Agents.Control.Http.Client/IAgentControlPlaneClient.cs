// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Control.Http;

/// <summary>
/// Typed HTTP client over the control-plane REST surface. Shape mirrors
/// <see cref="IAgentLifecycleManager"/> — the client is a client-side proxy for
/// the server, not a new surface. Consumer-facing so mocking in tests is trivial.
/// </summary>
/// <remarks>
/// <para>
/// <b>Idempotency (v0.11+).</b> Every write method has two overloads: the
/// original (preserved for source-compat) + a version accepting an explicit
/// <c>idempotencyKey</c>. When the server runs the idempotency middleware,
/// retries of the same key + same body replay the cached response; mismatched
/// bodies surface as <see cref="AgentControlPlaneException"/> with the
/// <c>urn:vais-agents:idempotency-mismatch</c> Problem Details type URN.
/// The DIM default on the key-accepting overload delegates to the original
/// method, silently dropping the key — mock implementations that don't track
/// keys work unchanged; the concrete <see cref="AgentControlPlaneClient"/>
/// threads the key onto the outgoing <c>Idempotency-Key</c> HTTP header.
/// </para>
/// </remarks>
public interface IAgentControlPlaneClient
{
    /// <summary>POST /v1/agents — register a manifest, get a handle.</summary>
    Task<AgentHandle> CreateAsync(AgentManifest manifest, CancellationToken cancellationToken = default);

    /// <summary>POST /v1/agents — register a manifest with an explicit idempotency key.</summary>
    Task<AgentHandle> CreateAsync(AgentManifest manifest, string? idempotencyKey, CancellationToken cancellationToken)
        => CreateAsync(manifest, cancellationToken);

    /// <summary>GET /v1/agents — list registered manifests with optional label-prefix filter.</summary>
    Task<IReadOnlyList<AgentManifest>> ListAsync(string? labelPrefix = null, int? limit = null, CancellationToken cancellationToken = default);

    /// <summary>GET /v1/agents/{id} — fetch manifest + current status.</summary>
    Task<AgentQueryResponse?> QueryAsync(string agentId, string? version = null, CancellationToken cancellationToken = default);

    /// <summary>PATCH /v1/agents/{id} — publish a new manifest version.</summary>
    Task<AgentHandle> UpdateAsync(string agentId, AgentManifest newManifest, string? version = null, CancellationToken cancellationToken = default);

    /// <summary>PATCH /v1/agents/{id} — publish a new manifest version with an explicit idempotency key.</summary>
    Task<AgentHandle> UpdateAsync(string agentId, AgentManifest newManifest, string? version, string? idempotencyKey, CancellationToken cancellationToken)
        => UpdateAsync(agentId, newManifest, version, cancellationToken);

    /// <summary>DELETE /v1/agents/{id}?mode=cancel — cancel in-flight work; handle remains valid.</summary>
    Task CancelAsync(string agentId, string? version = null, CancellationToken cancellationToken = default);

    /// <summary>DELETE /v1/agents/{id}?mode=cancel — cancel with an explicit idempotency key.</summary>
    Task CancelAsync(string agentId, string? version, string? idempotencyKey, CancellationToken cancellationToken)
        => CancelAsync(agentId, version, cancellationToken);

    /// <summary>DELETE /v1/agents/{id}?mode=evict — remove the manifest + state.</summary>
    Task EvictAsync(string agentId, string? version = null, CancellationToken cancellationToken = default);

    /// <summary>DELETE /v1/agents/{id}?mode=evict — evict with an explicit idempotency key.</summary>
    Task EvictAsync(string agentId, string? version, string? idempotencyKey, CancellationToken cancellationToken)
        => EvictAsync(agentId, version, cancellationToken);

    /// <summary>POST /v1/agents/{id}/invoke — synchronous invocation returning an assistant reply.</summary>
    Task<AgentInvocationResult> InvokeAsync(string agentId, AgentInvocationRequest request, string? version = null, CancellationToken cancellationToken = default);

    /// <summary>POST /v1/agents/{id}/invoke — synchronous invocation with an explicit idempotency key.</summary>
    Task<AgentInvocationResult> InvokeAsync(string agentId, AgentInvocationRequest request, string? version, string? idempotencyKey, CancellationToken cancellationToken)
        => InvokeAsync(agentId, request, version, cancellationToken);

    /// <summary>POST /v1/agents/{id}/signal — fire-and-forget signal delivery.</summary>
    Task SignalAsync(string agentId, AgentSignal signal, string? version = null, CancellationToken cancellationToken = default);

    /// <summary>POST /v1/agents/{id}/signal — signal delivery with an explicit idempotency key.</summary>
    Task SignalAsync(string agentId, AgentSignal signal, string? version, string? idempotencyKey, CancellationToken cancellationToken)
        => SignalAsync(agentId, signal, version, cancellationToken);

    /// <summary>
    /// POST /v1/agents/{id}/invoke/stream — stream an invocation as SSE, yielding
    /// only <see cref="CompletionDelta.TextDelta"/> values. Filters the full event
    /// stream to text. Default implementation throws <see cref="NotSupportedException"/>
    /// so mock implementations don't need to handle streaming.
    /// </summary>
    IAsyncEnumerable<string> InvokeStreamAsync(
        string agentId,
        AgentInvocationRequest request,
        string? version,
        string? idempotencyKey,
        CancellationToken cancellationToken)
        => throw new NotSupportedException(
            "This IAgentControlPlaneClient implementation does not support streaming invoke. " +
            "Use AgentControlPlaneClient (shipped) or override this method.");

    /// <summary>
    /// POST /v1/agents/{id}/invoke/stream — stream an invocation as SSE, yielding
    /// the full <see cref="AgentEvent"/> taxonomy. Default implementation throws
    /// <see cref="NotSupportedException"/>.
    /// </summary>
    IAsyncEnumerable<AgentEvent> InvokeStreamEventsAsync(
        string agentId,
        AgentInvocationRequest request,
        string? version,
        string? idempotencyKey,
        CancellationToken cancellationToken)
        => throw new NotSupportedException(
            "This IAgentControlPlaneClient implementation does not support streaming invoke. " +
            "Use AgentControlPlaneClient (shipped) or override this method.");

    // ── Graph verbs (v0.19) ─────────────────────────────────────────────────

    /// <summary>POST /v1/graphs — register a graph manifest, get a handle.</summary>
    Task<AgentGraphHandle> CreateGraphAsync(AgentGraphManifest manifest, CancellationToken cancellationToken = default);

    /// <summary>POST /v1/graphs — register a graph manifest with an explicit idempotency key.</summary>
    Task<AgentGraphHandle> CreateGraphAsync(AgentGraphManifest manifest, string? idempotencyKey, CancellationToken cancellationToken)
        => CreateGraphAsync(manifest, cancellationToken);

    /// <summary>GET /v1/graphs — list registered graph manifests with optional label-prefix filter.</summary>
    Task<AgentGraphListResponse> ListGraphsAsync(string? labelPrefix = null, int? limit = null, string? cursor = null, CancellationToken cancellationToken = default);

    /// <summary>GET /v1/graphs/{id} — fetch graph manifest + current status.</summary>
    Task<AgentGraphQueryResponse?> QueryGraphAsync(string graphId, string? version = null, CancellationToken cancellationToken = default);

    /// <summary>PATCH /v1/graphs/{id} — publish a new graph manifest version.</summary>
    Task<AgentGraphHandle> UpdateGraphAsync(string graphId, AgentGraphManifest newManifest, string? version = null, CancellationToken cancellationToken = default);

    /// <summary>PATCH /v1/graphs/{id} — publish a new graph manifest version with an explicit idempotency key.</summary>
    Task<AgentGraphHandle> UpdateGraphAsync(string graphId, AgentGraphManifest newManifest, string? version, string? idempotencyKey, CancellationToken cancellationToken)
        => UpdateGraphAsync(graphId, newManifest, version, cancellationToken);

    /// <summary>DELETE /v1/graphs/{id} — remove graph manifest + state.</summary>
    Task EvictGraphAsync(string graphId, string? version = null, CancellationToken cancellationToken = default);

    /// <summary>DELETE /v1/graphs/{id} — evict with an explicit idempotency key.</summary>
    Task EvictGraphAsync(string graphId, string? version, string? idempotencyKey, CancellationToken cancellationToken)
        => EvictGraphAsync(graphId, version, cancellationToken);

    /// <summary>POST /v1/graphs/{id}/invoke — synchronous graph invocation returning final state.</summary>
    Task<GraphInvocationResult> InvokeGraphAsync(string graphId, GraphInvocationRequest request, string? version = null, CancellationToken cancellationToken = default);

    /// <summary>POST /v1/graphs/{id}/invoke — synchronous graph invocation with an explicit idempotency key.</summary>
    Task<GraphInvocationResult> InvokeGraphAsync(string graphId, GraphInvocationRequest request, string? version, string? idempotencyKey, CancellationToken cancellationToken)
        => InvokeGraphAsync(graphId, request, version, cancellationToken);

    /// <summary>
    /// POST /v1/graphs/{id}/invoke/stream — stream a graph invocation as SSE, yielding
    /// <see cref="AgentGraphEvent"/> subtypes. Default implementation throws
    /// <see cref="NotSupportedException"/>.
    /// </summary>
    IAsyncEnumerable<AgentGraphEvent> InvokeGraphStreamAsync(
        string graphId,
        GraphInvocationRequest request,
        string? version,
        string? idempotencyKey,
        CancellationToken cancellationToken)
        => throw new NotSupportedException(
            "This IAgentControlPlaneClient implementation does not support graph streaming invoke. " +
            "Use AgentControlPlaneClient (shipped) or override this method.");

    /// <summary>POST /v1/graphs/{id}/runs/{runId}/resume — resume an interrupted graph run.</summary>
    Task<GraphInvocationResult> ResumeGraphAsync(string graphId, string runId, GraphResumeRequest request, string? version = null, CancellationToken cancellationToken = default);

    /// <summary>POST /v1/graphs/{id}/runs/{runId}/resume — resume with an explicit idempotency key.</summary>
    Task<GraphInvocationResult> ResumeGraphAsync(string graphId, string runId, GraphResumeRequest request, string? version, string? idempotencyKey, CancellationToken cancellationToken)
        => ResumeGraphAsync(graphId, runId, request, version, cancellationToken);

    /// <summary>
    /// POST /v1/graphs/{id}/runs/{runId}/resume/stream — stream resume of an interrupted graph run
    /// as SSE, yielding <see cref="AgentGraphEvent"/> subtypes. Default implementation throws
    /// <see cref="NotSupportedException"/>.
    /// </summary>
    IAsyncEnumerable<AgentGraphEvent> ResumeGraphStreamAsync(
        string graphId,
        string runId,
        GraphResumeRequest request,
        string? version,
        string? idempotencyKey,
        CancellationToken cancellationToken)
        => throw new NotSupportedException(
            "This IAgentControlPlaneClient implementation does not support graph streaming resume. " +
            "Use AgentControlPlaneClient (shipped) or override this method.");

    /// <summary>DELETE /v1/graphs/{id}/runs/{runId} — cancel a specific graph run.</summary>
    Task CancelGraphRunAsync(string graphId, string runId, string? version = null, CancellationToken cancellationToken = default);

    // ── Graph validation (v0.38) ───────────────────────────────────────────

    /// <summary>
    /// POST /v1/graphs/validate — dry-run structural and runtime-context validation
    /// of a graph manifest without registering it. Always returns a result; inspect
    /// <see cref="GraphValidationResult.Valid"/> for the outcome.
    /// Default implementation returns a passing result so mock clients don't need to override.
    /// </summary>
    Task<GraphValidationResult> ValidateGraphAsync(AgentGraphManifest manifest, CancellationToken cancellationToken = default)
        => Task.FromResult(new GraphValidationResult(Valid: true, Array.Empty<string>()));

    // ── Run history (RS-8) ─────────────────────────────────────────────────

    /// <summary>GET /v1/graphs/{id}/runs — list historical runs. Returns empty list when run store is not configured.</summary>
    Task<RunListResponse> ListRunsAsync(
        string graphId,
        string? status = null,
        DateTimeOffset? since = null,
        DateTimeOffset? until = null,
        int limit = 20,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new RunListResponse(Array.Empty<PipelineRunDto>()));

    /// <summary>GET /v1/graphs/{id}/runs/{runId} — fetch a single run. Returns null on 404 or when run store is not configured.</summary>
    Task<PipelineRunDto?> GetRunAsync(string graphId, string runId, CancellationToken cancellationToken = default)
        => Task.FromResult((PipelineRunDto?)null);

    /// <summary>GET /v1/graphs/{id}/runs/{runId}/nodes — list node executions. Returns empty list when run store is not configured.</summary>
    Task<IReadOnlyList<NodeExecutionDto>> GetRunNodesAsync(string graphId, string runId, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<NodeExecutionDto>>(Array.Empty<NodeExecutionDto>());

    /// <summary>GET /v1/graphs/{id}/runs/{runId}/nodes/{nodeId} — fetch a single node execution. Returns null on 404 or when run store is not configured.</summary>
    Task<NodeExecutionDto?> GetRunNodeAsync(string graphId, string runId, string nodeId, CancellationToken cancellationToken = default)
        => Task.FromResult((NodeExecutionDto?)null);

    // ── Runtime topology (v0.34) ────────────────────────────────────────────

    /// <summary>
    /// GET /v1/runtimes — list remote runtimes configured on this host. Returns an empty
    /// response when the server has no remote runtimes registered (no error).
    /// </summary>
    Task<RuntimeListResponse> GetRemoteRuntimesAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(new RuntimeListResponse(Array.Empty<RuntimeInfo>()));

    // ── Plugin introspection (v0.35) ────────────────────────────────────────

    /// <summary>
    /// GET /v1/plugins — list all loaded plugins (both .NET assembly and Python subprocess).
    /// Returns an empty response when no plugins are loaded (no error).
    /// </summary>
    Task<PluginListResponse> ListPluginsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(new PluginListResponse(Array.Empty<PluginInfo>()));

    /// <summary>
    /// POST /v1/plugins/{name}/source — push a tar.gz archive of plugin source into the runtime
    /// and trigger a DrainAndSwap reload. The stream must be a valid gzip-compressed tar archive.
    /// Returns <see cref="PluginSourcePushStatus.ReloadDisabled"/> (503) when hot-reload is
    /// not enabled on the target runtime.
    /// </summary>
    Task<PluginSourcePushResponse> PushPluginSourceAsync(
        string pluginName,
        Stream sourceTarGz,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new PluginSourcePushResponse(
            pluginName, PluginSourcePushStatus.ReloadDisabled, null, "Not supported by this client."));

    /// <summary>
    /// POST /v1/plugins/{name}/image — replace a container plugin with a new image and trigger
    /// a drain/replace hot-reload cycle. Returns 503 when container plugins are not configured.
    /// </summary>
    Task<PluginImageUpdateResponse> PushPluginImageAsync(
        string pluginName,
        string image,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new PluginImageUpdateResponse(
            pluginName, PluginImageUpdateStatus.NoSupervisor, "Not supported by this client."));

    // ── LLM gateway config verbs (GCF-13) ──────────────────────────────────────

    /// <summary>POST /v1/llm-gateways — register a manifest, get a handle.</summary>
    Task<LlmGatewayConfigHandle> CreateLlmGatewayConfigAsync(LlmGatewayConfigManifest manifest, CancellationToken cancellationToken = default);

    /// <summary>POST /v1/llm-gateways — register a manifest with an explicit idempotency key.</summary>
    Task<LlmGatewayConfigHandle> CreateLlmGatewayConfigAsync(LlmGatewayConfigManifest manifest, string? idempotencyKey, CancellationToken cancellationToken)
        => CreateLlmGatewayConfigAsync(manifest, cancellationToken);

    /// <summary>PATCH /v1/llm-gateways/{id} — publish a new manifest version.</summary>
    Task<LlmGatewayConfigHandle> UpdateLlmGatewayConfigAsync(string id, LlmGatewayConfigManifest manifest, string? version = null, CancellationToken cancellationToken = default);

    /// <summary>PATCH /v1/llm-gateways/{id} — publish a new manifest version with an explicit idempotency key.</summary>
    Task<LlmGatewayConfigHandle> UpdateLlmGatewayConfigAsync(string id, LlmGatewayConfigManifest manifest, string? version, string? idempotencyKey, CancellationToken cancellationToken)
        => UpdateLlmGatewayConfigAsync(id, manifest, version, cancellationToken);

    /// <summary>GET /v1/llm-gateways — list registered manifests.</summary>
    Task<LlmGatewayConfigListResponse> ListLlmGatewayConfigsAsync(string? labelPrefix = null, int? limit = null, string? cursor = null, CancellationToken cancellationToken = default);

    /// <summary>GET /v1/llm-gateways/{id} — fetch manifest + current status. Returns null on 404.</summary>
    Task<LlmGatewayConfigQueryResponse?> QueryLlmGatewayConfigAsync(string id, string? version = null, CancellationToken cancellationToken = default);

    /// <summary>DELETE /v1/llm-gateways/{id} — remove the manifest.</summary>
    Task EvictLlmGatewayConfigAsync(string id, string? version = null, CancellationToken cancellationToken = default);

    /// <summary>POST /v1/llm-gateways/validate — dry-run validation without registering. Default: always valid.</summary>
    Task<LlmGatewayConfigValidationResult> ValidateLlmGatewayConfigAsync(LlmGatewayConfigManifest manifest, CancellationToken cancellationToken = default)
        => Task.FromResult(new LlmGatewayConfigValidationResult(Valid: true, Array.Empty<string>()));

    // ── MCP gateway config verbs (GCF-13) ──────────────────────────────────────

    /// <summary>POST /v1/mcp-gateways — register a manifest, get a handle.</summary>
    Task<McpGatewayConfigHandle> CreateMcpGatewayConfigAsync(McpGatewayConfigManifest manifest, CancellationToken cancellationToken = default);

    /// <summary>POST /v1/mcp-gateways — register a manifest with an explicit idempotency key.</summary>
    Task<McpGatewayConfigHandle> CreateMcpGatewayConfigAsync(McpGatewayConfigManifest manifest, string? idempotencyKey, CancellationToken cancellationToken)
        => CreateMcpGatewayConfigAsync(manifest, cancellationToken);

    /// <summary>PATCH /v1/mcp-gateways/{id} — publish a new manifest version.</summary>
    Task<McpGatewayConfigHandle> UpdateMcpGatewayConfigAsync(string id, McpGatewayConfigManifest manifest, string? version = null, CancellationToken cancellationToken = default);

    /// <summary>PATCH /v1/mcp-gateways/{id} — publish a new manifest version with an explicit idempotency key.</summary>
    Task<McpGatewayConfigHandle> UpdateMcpGatewayConfigAsync(string id, McpGatewayConfigManifest manifest, string? version, string? idempotencyKey, CancellationToken cancellationToken)
        => UpdateMcpGatewayConfigAsync(id, manifest, version, cancellationToken);

    /// <summary>GET /v1/mcp-gateways — list registered manifests.</summary>
    Task<McpGatewayConfigListResponse> ListMcpGatewayConfigsAsync(string? labelPrefix = null, int? limit = null, string? cursor = null, CancellationToken cancellationToken = default);

    /// <summary>GET /v1/mcp-gateways/{id} — fetch manifest + current status. Returns null on 404.</summary>
    Task<McpGatewayConfigQueryResponse?> QueryMcpGatewayConfigAsync(string id, string? version = null, CancellationToken cancellationToken = default);

    /// <summary>DELETE /v1/mcp-gateways/{id} — remove the manifest.</summary>
    Task EvictMcpGatewayConfigAsync(string id, string? version = null, CancellationToken cancellationToken = default);

    /// <summary>POST /v1/mcp-gateways/validate — dry-run validation without registering. Default: always valid.</summary>
    Task<McpGatewayConfigValidationResult> ValidateMcpGatewayConfigAsync(McpGatewayConfigManifest manifest, CancellationToken cancellationToken = default)
        => Task.FromResult(new McpGatewayConfigValidationResult(Valid: true, Array.Empty<string>()));

    // ── MCP server verbs (GCF-13) ───────────────────────────────────────────────

    /// <summary>POST /v1/mcp-servers — register a manifest, get a handle.</summary>
    Task<McpServerHandle> CreateMcpServerAsync(McpServerManifest manifest, CancellationToken cancellationToken = default);

    /// <summary>POST /v1/mcp-servers — register a manifest with an explicit idempotency key.</summary>
    Task<McpServerHandle> CreateMcpServerAsync(McpServerManifest manifest, string? idempotencyKey, CancellationToken cancellationToken)
        => CreateMcpServerAsync(manifest, cancellationToken);

    /// <summary>PATCH /v1/mcp-servers/{id} — publish a new manifest version.</summary>
    Task<McpServerHandle> UpdateMcpServerAsync(string id, McpServerManifest manifest, string? version = null, CancellationToken cancellationToken = default);

    /// <summary>PATCH /v1/mcp-servers/{id} — publish a new manifest version with an explicit idempotency key.</summary>
    Task<McpServerHandle> UpdateMcpServerAsync(string id, McpServerManifest manifest, string? version, string? idempotencyKey, CancellationToken cancellationToken)
        => UpdateMcpServerAsync(id, manifest, version, cancellationToken);

    /// <summary>GET /v1/mcp-servers — list registered manifests.</summary>
    Task<McpServerListResponse> ListMcpServersAsync(string? labelPrefix = null, int? limit = null, string? cursor = null, CancellationToken cancellationToken = default);

    /// <summary>GET /v1/mcp-servers/{id} — fetch manifest + current status. Returns null on 404.</summary>
    Task<McpServerQueryResponse?> QueryMcpServerAsync(string id, string? version = null, CancellationToken cancellationToken = default);

    /// <summary>DELETE /v1/mcp-servers/{id} — remove the manifest.</summary>
    Task EvictMcpServerAsync(string id, string? version = null, CancellationToken cancellationToken = default);

    /// <summary>POST /v1/mcp-servers/validate — dry-run validation without registering. Default: always valid.</summary>
    Task<McpServerValidationResult> ValidateMcpServerAsync(McpServerManifest manifest, CancellationToken cancellationToken = default)
        => Task.FromResult(new McpServerValidationResult(Valid: true, Array.Empty<string>()));

    // ── Container plugin verbs (v0.21) ──────────────────────────────────────

    /// <summary>POST /v1/container-plugins — register a manifest and start the container. Default: throws NotSupportedException.</summary>
    Task<ContainerPluginHandle> CreateContainerPluginAsync(ContainerPluginManifest manifest, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("This IAgentControlPlaneClient implementation does not support container plugin management. Use AgentControlPlaneClient.");

    /// <summary>POST /v1/container-plugins — register a manifest with an explicit idempotency key.</summary>
    Task<ContainerPluginHandle> CreateContainerPluginAsync(ContainerPluginManifest manifest, string? idempotencyKey, CancellationToken cancellationToken)
        => CreateContainerPluginAsync(manifest, cancellationToken);

    /// <summary>PATCH /v1/container-plugins/{id} — publish a new manifest version for an existing plugin. Default: throws NotSupportedException.</summary>
    Task<ContainerPluginHandle> UpdateContainerPluginAsync(string id, ContainerPluginManifest manifest, string? version = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("This IAgentControlPlaneClient implementation does not support container plugin management. Use AgentControlPlaneClient.");

    /// <summary>PATCH /v1/container-plugins/{id} — publish a new manifest version with an explicit idempotency key.</summary>
    Task<ContainerPluginHandle> UpdateContainerPluginAsync(string id, ContainerPluginManifest manifest, string? version, string? idempotencyKey, CancellationToken cancellationToken)
        => UpdateContainerPluginAsync(id, manifest, version, cancellationToken);

    /// <summary>GET /v1/container-plugins — list registered manifests. Default: empty list.</summary>
    Task<ContainerPluginListResponse> ListContainerPluginsAsync(string? labelPrefix = null, int? limit = null, string? cursor = null, CancellationToken cancellationToken = default)
        => Task.FromResult(new ContainerPluginListResponse(Array.Empty<ContainerPluginManifest>()));

    /// <summary>GET /v1/container-plugins/{id} — fetch manifest + current runtime status. Default: null (not found).</summary>
    Task<ContainerPluginQueryResponse?> QueryContainerPluginAsync(string id, string? version = null, CancellationToken cancellationToken = default)
        => Task.FromResult((ContainerPluginQueryResponse?)null);

    /// <summary>DELETE /v1/container-plugins/{id} — stop and remove the plugin. Default: throws NotSupportedException.</summary>
    Task EvictContainerPluginAsync(string id, string? version = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("This IAgentControlPlaneClient implementation does not support container plugin management. Use AgentControlPlaneClient.");

    /// <summary>POST /v1/container-plugins/validate — dry-run validation without registering. Default: always valid.</summary>
    Task<ContainerPluginValidationResult> ValidateContainerPluginAsync(ContainerPluginManifest manifest, CancellationToken cancellationToken = default)
        => Task.FromResult(new ContainerPluginValidationResult(Valid: true, Array.Empty<string>()));
}
