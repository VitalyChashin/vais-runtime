// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Control;

/// <summary>
/// Control-plane lifecycle manager for <see cref="IAgentGraphRegistry"/>-hosted graphs.
/// Mirrors <see cref="IAgentLifecycleManager"/> for agents, adding run + resume verbs
/// specific to the stateful, checkpointable graph execution model.
/// </summary>
/// <remarks>
/// <para>
/// All verbs are policy-gated (via <see cref="IAgentPolicyEngine"/>) and audited
/// (via <see cref="IAuditLog"/>). Implementors must call policy before performing
/// any state mutation and emit an <see cref="AuditLogEntry"/> on every verb regardless
/// of outcome.
/// </para>
/// <para>
/// <b>Graph vs run identity.</b> An <see cref="AgentGraphHandle"/> identifies a
/// (manifest-id, version) pair — the deployed graph definition. A run-id string
/// identifies a single execution of that graph. The pair is always required for
/// cancel/resume; the handle alone is required for create/update/query/evict.
/// </para>
/// </remarks>
public interface IAgentGraphLifecycleManager
{
    /// <summary>Register a new graph manifest, making it available for invocation.</summary>
    /// <returns>A stable handle pinned to the manifest's (id, version) pair.</returns>
    ValueTask<AgentGraphHandle> CreateAsync(AgentGraphManifest manifest, CancellationToken ct = default);

    /// <summary>
    /// Replace an existing manifest's definition. Live runs continue against the
    /// previous version until they complete; new runs use <paramref name="newManifest"/>.
    /// </summary>
    ValueTask<AgentGraphHandle> UpdateAsync(AgentGraphHandle handle, AgentGraphManifest newManifest, CancellationToken ct = default);

    /// <summary>Return current operational counters for the graph.</summary>
    /// <exception cref="GraphHandleNotFoundException">Graph handle was not found in the registry.</exception>
    ValueTask<AgentGraphStatus> QueryAsync(AgentGraphHandle handle, CancellationToken ct = default);

    /// <summary>
    /// Start a new graph run synchronously (blocking until terminal or interrupt).
    /// Returns a <see cref="GraphInvocationResult"/> whose <see cref="GraphInvocationResult.IsComplete"/>
    /// indicates whether the run hit an <c>End</c> node or an <c>Interrupt</c> node.
    /// </summary>
    /// <exception cref="GraphHandleNotFoundException">Handle not found.</exception>
    /// <exception cref="GraphRunConflictException">
    /// A run with the same <see cref="GraphInvocationRequest.RunId"/> is already in flight.
    /// </exception>
    ValueTask<GraphInvocationResult> InvokeAsync(AgentGraphHandle handle, GraphInvocationRequest request, CancellationToken ct = default);

    /// <summary>
    /// Start a new graph run and stream <see cref="AgentGraphEvent"/> objects in
    /// order as they are emitted by the orchestrator.
    /// </summary>
    /// <exception cref="GraphHandleNotFoundException">Handle not found.</exception>
    /// <exception cref="GraphRunConflictException">Duplicate run-id.</exception>
    IAsyncEnumerable<AgentGraphEvent> InvokeStreamAsync(AgentGraphHandle handle, GraphInvocationRequest request, CancellationToken ct = default);

    /// <summary>
    /// Resume a previously-interrupted graph run. The
    /// (<see cref="GraphResumeRequest.RunId"/>, <see cref="GraphResumeRequest.InterruptId"/>)
    /// pair must match the checkpoint; a mismatch is rejected.
    /// </summary>
    /// <exception cref="GraphHandleNotFoundException">Handle not found.</exception>
    /// <exception cref="GraphRunNotFoundException">No run with the given run-id exists.</exception>
    /// <exception cref="GraphInterruptMismatchException">Interrupt-id does not match the paused checkpoint.</exception>
    /// <exception cref="GraphAlreadyCompleteException">Run already reached a terminal node.</exception>
    ValueTask<GraphInvocationResult> ResumeAsync(AgentGraphHandle handle, GraphResumeRequest request, CancellationToken ct = default);

    /// <summary>Stream events from a resumed graph run.</summary>
    /// <exception cref="GraphHandleNotFoundException">Handle not found.</exception>
    /// <exception cref="GraphRunNotFoundException">No run with the given run-id exists.</exception>
    /// <exception cref="GraphInterruptMismatchException">Interrupt-id mismatch.</exception>
    /// <exception cref="GraphAlreadyCompleteException">Run already complete.</exception>
    IAsyncEnumerable<AgentGraphEvent> ResumeStreamAsync(AgentGraphHandle handle, GraphResumeRequest request, CancellationToken ct = default);

    /// <summary>
    /// Cancel an in-flight or interrupted run. The manifest remains registered;
    /// only the run is terminated. No-op when the run is already complete.
    /// </summary>
    /// <exception cref="GraphHandleNotFoundException">Handle not found.</exception>
    /// <exception cref="GraphRunNotFoundException">No run with the given run-id exists.</exception>
    ValueTask CancelAsync(AgentGraphHandle handle, string runId, CancellationToken ct = default);

    /// <summary>
    /// Remove the graph manifest and all associated run state. In-flight runs are
    /// cancelled before eviction.
    /// </summary>
    /// <exception cref="GraphHandleNotFoundException">Handle not found.</exception>
    ValueTask EvictAsync(AgentGraphHandle handle, CancellationToken ct = default);
}
