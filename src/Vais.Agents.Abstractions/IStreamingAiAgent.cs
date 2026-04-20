// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents;

/// <summary>
/// Capability interface for agents that can stream a turn as a sequence of
/// <see cref="AgentEvent"/>s. Complementary to <see cref="IAiAgent"/>; an agent
/// implementation is free to support one, the other, or both. The concrete
/// <c>StatefulAiAgent</c> implements both.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a capability interface rather than adding to <see cref="IAiAgent"/>.</b>
/// Not every <see cref="IAiAgent"/> can stream — Orleans-grain-proxied agents in
/// particular don't today (the Orleans runtime's return-value-only grain
/// contract doesn't fit <see cref="IAsyncEnumerable{T}"/> without custom
/// streaming hooks). Exposing streaming as an optional capability lets
/// consumers check <c>agent is IStreamingAiAgent streamable</c> and fall back to
/// <see cref="IAiAgent.AskAsync"/> or surface a 501 when unavailable.
/// </para>
/// <para>
/// <b>Event ordering contract.</b> <see cref="StreamAsync"/> yields, in order:
/// <list type="number">
///   <item>A single <see cref="TurnStarted"/>.</item>
///   <item>One or more <see cref="CompletionDelta"/>s as the provider streams
///   text. Interleaved with <see cref="ToolCallStarted"/> / <see cref="ToolCallCompleted"/>
///   pairs when the provider requests tools and the agent dispatches them on
///   the caller's behalf (same behaviour as <c>StatefulAiAgent</c>'s tool-call
///   loop in non-streaming mode).</item>
///   <item>Optional <see cref="GuardrailTriggered"/> /
///   <see cref="InterruptRaised"/> / <see cref="ToolCallReplayed"/> events when
///   the relevant pillars fire.</item>
///   <item>A single terminal <see cref="TurnCompleted"/> <b>or</b>
///   <see cref="TurnFailed"/>, then the enumerable ends.</item>
/// </list>
/// </para>
/// <para>
/// <b>Cancellation.</b> Cancelling the <c>cancellationToken</c> passed to
/// <see cref="StreamAsync"/> causes the enumerable to end <em>without</em>
/// emitting <see cref="TurnFailed"/> — <see cref="System.OperationCanceledException"/>
/// is not a turn failure, matching the non-streaming discipline. Handler
/// exceptions that aren't cancellations emit <see cref="TurnFailed"/> as the
/// final event and then propagate.
/// </para>
/// <para>
/// <b>Relationship to <see cref="IAgentEventBus"/>.</b> Implementations consumed
/// via <see cref="IStreamingAiAgent.StreamAsync"/> should <b>not</b> also
/// publish the same events to <see cref="IAgentEventBus"/>; the caller
/// observes them directly. Event-bus fan-out is the domain of
/// <see cref="IAiAgent.AskAsync"/> (non-streaming) where no other observer
/// exists.
/// </para>
/// </remarks>
public interface IStreamingAiAgent
{
    /// <summary>
    /// Execute a streaming turn against the agent. Yields the full
    /// <see cref="AgentEvent"/> taxonomy in ordering-contract order
    /// (see the interface remarks). Yielded deltas are committed — consumers
    /// that partially consume the enumerable and drop the rest do not retract
    /// the side-effects (history writes, tool dispatches) the emitted events
    /// describe.
    /// </summary>
    /// <param name="userMessage">
    /// User-visible text to send as the new turn. Must be non-empty; the agent
    /// may throw <see cref="System.ArgumentException"/> for whitespace-only input.
    /// </param>
    /// <param name="context">
    /// Ambient agent context at call entry. Populates <see cref="AgentEvent.Context"/>
    /// on every emitted event; the agent may overlay its own fields (e.g.
    /// <c>AgentName</c>, <c>RunId</c>) before propagating.
    /// </param>
    /// <param name="cancellationToken">
    /// Cancels the stream. Cancelled mid-stream ends the enumerable cleanly
    /// without a final <see cref="TurnFailed"/>.
    /// </param>
    IAsyncEnumerable<AgentEvent> StreamAsync(
        string userMessage,
        AgentContext context,
        CancellationToken cancellationToken);
}
