// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents;

/// <summary>
/// Marks an <see cref="IAgentGraph{TState}"/> implementation as capable of live
/// human-in-the-loop (HITL) interrupts. Unlike <see cref="IResumableAgentGraph{TState}"/>
/// (which halts the workflow and resumes from a persisted <see cref="GraphCheckpoint"/>),
/// this interface keeps the graph run open during each <c>Interrupt</c>-kind node,
/// invokes the supplied handler inline within the streaming enumeration, and feeds the
/// response back to the running graph without crossing a process boundary.
/// </summary>
/// <remarks>
/// <para>
/// <b>Handler semantics.</b> For each <see cref="GraphInterrupted"/> event the handler
/// is awaited before the graph advances. The event includes
/// <see cref="GraphInterrupted.CurrentState"/> — the accumulated graph state at interruption
/// time — so handlers can incorporate prior computed values (e.g., a generated draft) in the
/// human-facing prompt.
/// Returning a non-null <typeparamref name="TState"/> provides the <em>HITL response payload</em>:
/// it is serialised and merged under the well-known <c>"hitl.response"</c> state key, then
/// evaluation of the interrupt node's outgoing edges continues.
/// Returning <see langword="null"/> aborts the run: <see cref="GraphFailed"/> is emitted and
/// <see cref="GraphHitlAbortedException"/> is thrown.
/// </para>
/// <para>
/// <b>Multiple sequential interrupts.</b> The callback is invoked once per interrupt in
/// graph-traversal order. Each call gets a fresh <see cref="GraphInterrupted"/> event
/// with its own <see cref="GraphInterrupted.InterruptId"/>. Returning non-null each time
/// threads responses forward through the graph.
/// </para>
/// <para>
/// <b>Why a separate interface?</b> Orthogonal to both <see cref="IAgentGraph{TState}"/>
/// (basic run/stream) and <see cref="IResumableAgentGraph{TState}"/> (durable resume).
/// Not every orchestrator backend can keep a live run open during an interrupt — the MAF
/// adapter requires <c>InProcessExecution.OffThread</c> and <c>RequestPort</c> wiring.
/// Consumers query <c>graph is IHitlAgentGraph&lt;TState&gt;</c> at runtime to detect support.
/// </para>
/// </remarks>
/// <typeparam name="TState">Graph state type — same type parameter as <see cref="IAgentGraph{TState}"/>.</typeparam>
public interface IHitlAgentGraph<TState>
{
    /// <summary>
    /// Stream graph events with inline HITL handling. At each <c>Interrupt</c>-kind node
    /// the supplied <paramref name="handleInterrupt"/> callback is awaited before advancing;
    /// its non-null return value is merged under <c>"hitl.response"</c> in graph state.
    /// Returns <see langword="null"/> to abort with <see cref="GraphHitlAbortedException"/>.
    /// </summary>
    /// <param name="initial">Initial graph state.</param>
    /// <param name="context">Ambient agent context stamped on events.</param>
    /// <param name="handleInterrupt">
    /// Async callback invoked at each <see cref="GraphInterrupted"/> event.
    /// Return the updated state to continue; return <see langword="null"/> to abort.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    IAsyncEnumerable<AgentGraphEvent> StreamWithHitlAsync(
        TState initial,
        AgentContext context,
        Func<GraphInterrupted, CancellationToken, ValueTask<TState?>> handleInterrupt,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Run the graph with inline HITL handling and return the final state. Drains the
    /// event stream; throws <see cref="GraphHitlAbortedException"/> if
    /// <paramref name="handleInterrupt"/> returns <see langword="null"/>.
    /// </summary>
    /// <param name="initial">Initial graph state.</param>
    /// <param name="context">Ambient agent context stamped on events.</param>
    /// <param name="handleInterrupt">
    /// Async callback invoked at each <see cref="GraphInterrupted"/> event.
    /// Return the updated state to continue; return <see langword="null"/> to abort.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask<TState> InvokeWithHitlAsync(
        TState initial,
        AgentContext context,
        Func<GraphInterrupted, CancellationToken, ValueTask<TState?>> handleInterrupt,
        CancellationToken cancellationToken = default);
}
