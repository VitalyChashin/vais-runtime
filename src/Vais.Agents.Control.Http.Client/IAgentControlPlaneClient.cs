// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Control.Http;

/// <summary>
/// Typed HTTP client over the control-plane REST surface. Shape mirrors
/// <see cref="IAgentLifecycleManager"/> — the client is a client-side proxy for
/// the server, not a new surface. Consumer-facing so mocking in tests is trivial.
/// </summary>
public interface IAgentControlPlaneClient
{
    /// <summary>POST /v1/agents — register a manifest, get a handle.</summary>
    Task<AgentHandle> CreateAsync(AgentManifest manifest, CancellationToken cancellationToken = default);

    /// <summary>GET /v1/agents — list registered manifests with optional label-prefix filter.</summary>
    Task<IReadOnlyList<AgentManifest>> ListAsync(string? labelPrefix = null, int? limit = null, CancellationToken cancellationToken = default);

    /// <summary>GET /v1/agents/{id} — fetch manifest + current status.</summary>
    Task<AgentQueryResponse?> QueryAsync(string agentId, string? version = null, CancellationToken cancellationToken = default);

    /// <summary>PATCH /v1/agents/{id} — publish a new manifest version.</summary>
    Task<AgentHandle> UpdateAsync(string agentId, AgentManifest newManifest, string? version = null, CancellationToken cancellationToken = default);

    /// <summary>DELETE /v1/agents/{id}?mode=cancel — cancel in-flight work; handle remains valid.</summary>
    Task CancelAsync(string agentId, string? version = null, CancellationToken cancellationToken = default);

    /// <summary>DELETE /v1/agents/{id}?mode=evict — remove the manifest + state.</summary>
    Task EvictAsync(string agentId, string? version = null, CancellationToken cancellationToken = default);

    /// <summary>POST /v1/agents/{id}/invoke — synchronous invocation returning an assistant reply.</summary>
    Task<AgentInvocationResult> InvokeAsync(string agentId, AgentInvocationRequest request, string? version = null, CancellationToken cancellationToken = default);

    /// <summary>POST /v1/agents/{id}/signal — fire-and-forget signal delivery.</summary>
    Task SignalAsync(string agentId, AgentSignal signal, string? version = null, CancellationToken cancellationToken = default);
}
