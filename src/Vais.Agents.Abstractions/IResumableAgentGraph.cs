// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents;

/// <summary>
/// Marks an <see cref="IAgentGraph{TState}"/> implementation as capable of resuming
/// from a persisted <see cref="GraphCheckpoint"/>. Paired with <see cref="IGraphCheckpointer"/>
/// to give consumers durable pause/resume on <c>Interrupt</c>-kind nodes — the v0.9
/// analogue of A2A's <c>Task(input-required)</c> resume pattern.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a separate interface?</b> Resume is a capability, not something every
/// <see cref="IAgentGraph{TState}"/> implementation must provide. Both shipped orchestrators
/// support it: <c>InProcessGraphOrchestrator</c> and <c>MafGraphOrchestrator</c> — the latter
/// bridges resume through this <see cref="GraphCheckpoint"/> shape via its
/// <c>ResumeFromNodeId</c> message flag (it re-enters the interrupt node's executor and skips
/// the body), rather than using MAF's own <c>CheckpointManager</c> format. Keeping resume on a
/// capability interface lets consumers query <c>graph is IResumableAgentGraph&lt;TState&gt;</c>
/// at runtime and lets future orchestrators opt out.
/// </para>
/// <para>
/// <b>Resume semantics.</b> The checkpoint captures state at the interrupt boundary.
/// Calling <c>ResumeAsync</c> loads that state, applies the caller's resume payload
/// under the well-known <c>"resume.payload"</c> key, and continues the graph from
/// the interrupt node's outgoing edges — the interrupt node itself does NOT re-fire.
/// Re-entering an interrupt produces another <see cref="GraphCheckpoint"/> + a
/// fresh <see cref="GraphInterrupted"/> event, just like a two-step form.
/// </para>
/// </remarks>
public interface IResumableAgentGraph<TState>
{
    /// <summary>
    /// Resume a previously-interrupted run from <paramref name="checkpoint"/>. Applies
    /// <paramref name="resumePayload"/> to state under the well-known
    /// <c>"resume.payload"</c> key, then continues the graph past the interrupt node.
    /// </summary>
    /// <param name="checkpoint">Checkpoint loaded from an <see cref="IGraphCheckpointer"/>. Must have non-null <see cref="GraphCheckpoint.NextNodeId"/>.</param>
    /// <param name="resumePayload">The value the caller wants to supply at the interrupt boundary. Null merges nothing; non-null is serialised to JSON and placed under state's <c>"resume.payload"</c> key.</param>
    /// <param name="context">Ambient agent context stamped on events emitted during the resumed run.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask<TState> ResumeAsync(
        GraphCheckpoint checkpoint,
        TState? resumePayload,
        AgentContext context,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Streaming variant of <see cref="ResumeAsync"/> — yields the full
    /// <see cref="AgentGraphEvent"/> taxonomy starting with <see cref="GraphResumed"/>.
    /// </summary>
    IAsyncEnumerable<AgentGraphEvent> ResumeStreamAsync(
        GraphCheckpoint checkpoint,
        TState? resumePayload,
        AgentContext context,
        CancellationToken cancellationToken = default);
}
