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
}
