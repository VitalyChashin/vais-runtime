// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;

namespace Vais.Agents;

/// <summary>
/// Opaque handle to a lifecycle-managed agent instance. Three-tuple because
/// different runtimes key at different granularities — in-memory registries key
/// by <see cref="AgentId"/> + <see cref="Version"/>; session-grain runtimes need
/// an <see cref="InstanceId"/> when multiple instances of the same agent+version
/// coexist.
/// </summary>
public sealed record AgentHandle(string AgentId, string Version, string? InstanceId = null);

/// <summary>
/// Input to <see cref="IAgentLifecycleManager.InvokeAsync"/>. Stack-neutral wrapper
/// around the user-facing text plus ambient metadata (session id, correlation ids,
/// tenant tags).
/// </summary>
/// <param name="Text">User-visible message for the agent to process.</param>
/// <param name="SessionId">Optional session identifier. When null, the runtime chooses.</param>
/// <param name="Metadata">Arbitrary flat metadata — correlation ids, tenant, tracing tags.</param>
/// <param name="InitialHistory">Optional prior conversation turns to seed into the agent's history before processing <paramref name="Text"/>. When non-null, the agent resets its session and replays these turns first, enabling stateless multi-turn usage.</param>
public sealed record AgentInvocationRequest(
    string Text,
    string? SessionId = null,
    IReadOnlyDictionary<string, string>? Metadata = null,
    IReadOnlyList<(string Role, string Content)>? InitialHistory = null);

/// <summary>Result of an <see cref="IAgentLifecycleManager.InvokeAsync"/> call. Mirror shape of request.</summary>
/// <param name="Text">Assistant-produced text.</param>
/// <param name="SessionId">Session id the invocation ran under (either the supplied one or a runtime-assigned one).</param>
/// <param name="Metadata">Runtime-produced metadata (model id, tokens, duration-ms, etc.).</param>
public sealed record AgentInvocationResult(
    string Text,
    string? SessionId = null,
    IReadOnlyDictionary<string, string>? Metadata = null);

/// <summary>
/// Signal dispatched to a running agent — the "resume a waiting run with data"
/// primitive that every durable-execution runtime exposes (Temporal signal,
/// OpenAI submit_tool_outputs, Inngest event).
/// </summary>
/// <param name="Kind">Consumer-chosen string tag — e.g., "resume", "cancel-pending", "reload-config". Open by design; no enum.</param>
/// <param name="Payload">Signal payload. Shape determined by the consumer's signal handler.</param>
public sealed record AgentSignal(string Kind, JsonElement Payload);

/// <summary>
/// Status reported by <see cref="IAgentLifecycleManager.QueryAsync"/>. Five states
/// that cover the universal status set from the prior-art survey — AgentCore
/// endpoint states, Temporal workflow status, OpenAI Assistants run statuses.
/// Consumers extend by wrapping the enum in a richer domain-specific status type.
/// </summary>
public enum AgentStatus
{
    /// <summary>Handle has no known status (e.g., lifecycle manager hasn't seen it).</summary>
    Unknown = 0,

    /// <summary>Agent is running a turn or accepting work.</summary>
    Active = 1,

    /// <summary>Agent is created but idle — no in-flight work.</summary>
    Idle = 2,

    /// <summary>Agent is paused — typically waiting on an interrupt / human signal.</summary>
    Paused = 3,

    /// <summary>Agent has been cancelled, evicted, or completed terminally.</summary>
    Terminated = 4,
}

/// <summary>
/// Universal lifecycle verbs for an agent. Surveyed convergent verbs from AgentCore,
/// Temporal, Dapr Agents, OpenAI Assistants, and Inngest all fit here; consumers
/// wire their preferred backend.
/// </summary>
/// <remarks>
/// <b>No default implementation ships with v0.4.</b> Orleans-backed cloud-runtime
/// implementation lands in Phase 3 of the main research doc. In-process consumers
/// today compose <c>StatefulAiAgent</c> + <c>OrleansAgentRuntime</c> directly;
/// this interface is the contract for the eventual HTTP / CRD / cluster layer.
/// </remarks>
public interface IAgentLifecycleManager
{
    /// <summary>Create (or register) an agent from a manifest. Returns a handle for subsequent verbs.</summary>
    ValueTask<AgentHandle> CreateAsync(AgentManifest manifest, CancellationToken cancellationToken = default);

    /// <summary>Invoke the agent identified by <paramref name="handle"/> with a single request.</summary>
    ValueTask<AgentInvocationResult> InvokeAsync(AgentHandle handle, AgentInvocationRequest request, CancellationToken cancellationToken = default);

    /// <summary>Send a signal to a running agent — the universal "resume a waiting run with data" primitive.</summary>
    ValueTask SignalAsync(AgentHandle handle, AgentSignal signal, CancellationToken cancellationToken = default);

    /// <summary>Query the agent's current lifecycle status.</summary>
    ValueTask<AgentStatus> QueryAsync(AgentHandle handle, CancellationToken cancellationToken = default);

    /// <summary>Cancel in-flight work on the agent. Handle remains valid for status queries.</summary>
    ValueTask CancelAsync(AgentHandle handle, CancellationToken cancellationToken = default);

    /// <summary>Publish a new manifest version. Returns a new handle; existing in-flight runs continue on the old version.</summary>
    ValueTask<AgentHandle> UpdateAsync(AgentHandle handle, AgentManifest newManifest, CancellationToken cancellationToken = default);

    /// <summary>Evict the agent. Persistent state and the handle are invalidated.</summary>
    ValueTask EvictAsync(AgentHandle handle, CancellationToken cancellationToken = default);

    /// <summary>
    /// Evict a session-scoped agent created by <see cref="IAgentRuntime.GetOrCreateForSession"/>.
    /// Called by the graph orchestrator at run completion to release per-run grain state.
    /// Default: no-op (for implementations that do not support session isolation).
    /// </summary>
    virtual ValueTask EvictSessionAsync(string agentId, string sessionId, CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;
}

/// <summary>
/// Registry of agent manifests — the "what agents exist / what versions" lookup
/// surface. Separate from <see cref="IAgentLifecycleManager"/> so consumers can
/// wire a read-only registry (e.g., git-backed or config-driven) even without a
/// full lifecycle implementation.
/// </summary>
public interface IAgentRegistry
{
    /// <summary>Enumerate manifests, optionally filtered by a label prefix (e.g., "team:").</summary>
    IAsyncEnumerable<AgentManifest> ListAsync(string? labelPrefix = null, CancellationToken cancellationToken = default);

    /// <summary>Fetch a specific manifest. Null <paramref name="version"/> returns the latest-lexicographically version.</summary>
    ValueTask<AgentManifest?> GetAsync(string id, string? version = null, CancellationToken cancellationToken = default);
}

/// <summary>
/// Identity provider for the control plane — separates inbound authentication
/// (who's calling the agent?) from outbound credential acquisition (what token
/// does the agent use to call downstream services?). AgentCore's Identity
/// service is the shape this mirrors.
/// </summary>
public interface IAgentIdentityProvider
{
    /// <summary>Authenticate an inbound invocation. Returns the principal or throws on failure.</summary>
    ValueTask<AgentPrincipal> AuthenticateInboundAsync(AgentInvocationRequest request, CancellationToken cancellationToken = default);

    /// <summary>Acquire an outbound credential (e.g., OAuth token, API key) for the agent to use downstream.</summary>
    ValueTask<OutboundCredential> AcquireOutboundAsync(string agentId, string credentialRef, CancellationToken cancellationToken = default);
}

/// <summary>Authenticated caller identity for an inbound agent invocation.</summary>
/// <param name="Id">Stable principal identifier — user id, service-account id, or anonymous tag.</param>
/// <param name="TenantId">Optional tenant scope.</param>
/// <param name="Scopes">OAuth / ABAC scope list when the auth scheme supplies one.</param>
public sealed record AgentPrincipal(string Id, string? TenantId = null, IReadOnlyList<string>? Scopes = null);

/// <summary>Acquired outbound credential the agent uses to call downstream services.</summary>
/// <param name="Kind">Credential kind — "Bearer", "ApiKey", "BasicAuth", etc. Consumer-defined.</param>
/// <param name="Value">Credential value. Opaque to the library.</param>
/// <param name="ExpiresAt">Optional expiry for caching / refresh.</param>
public sealed record OutboundCredential(string Kind, string Value, DateTimeOffset? ExpiresAt = null);
