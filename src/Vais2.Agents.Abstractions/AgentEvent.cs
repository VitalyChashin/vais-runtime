// Copyright (c) 2026 VAIS2 Platform contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais2.Agents;

/// <summary>
/// Base type for semantic events emitted by an <see cref="IAiAgent"/> as it processes
/// turns. Complement to <see cref="IUsageSink"/>: usage sink is numeric telemetry
/// (tokens, duration, success/fail) for aggregation; agent events carry the semantic
/// payload (user message, assistant text, error) for event-driven consumers
/// (UIs, integrations, audit logs, cross-process fan-out via Redis streams).
/// </summary>
/// <remarks>
/// <para>
/// Closed hierarchy: the Abstractions package defines exactly these three concrete
/// shapes. Consumers pattern-match on subtype; adding a new subtype is an
/// <em>unshipped</em> addition to Abstractions, not a subclass in downstream code.
/// Keeps wire serialisation (Orleans, Redis streams) deterministic.
/// </para>
/// <para>
/// Tool-invocation events are intentionally NOT yet represented — surfacing them
/// requires adapter-side hooks inside SK's auto-invoke connector and MAF's
/// <c>FunctionInvokingChatClient</c>. Deferred to a later milestone.
/// </para>
/// </remarks>
/// <param name="At">UTC timestamp when the event was emitted.</param>
/// <param name="Context">Ambient agent context at event-emission time.</param>
public abstract record AgentEvent(DateTimeOffset At, AgentContext Context);

/// <summary>
/// Emitted before the provider is invoked for a turn. The user's message has been
/// appended to the agent's history; the assistant reply has not yet been produced.
/// </summary>
public sealed record TurnStarted(
    DateTimeOffset At,
    AgentContext Context,
    string UserMessage)
    : AgentEvent(At, Context);

/// <summary>
/// Emitted after a turn completes successfully. The assistant's reply has been
/// appended to the agent's history and usage telemetry has been reported.
/// </summary>
/// <param name="At">UTC timestamp when the event was emitted.</param>
/// <param name="Context">Ambient agent context at event-emission time.</param>
/// <param name="AssistantText">Full assistant-produced text for the turn.</param>
/// <param name="ModelId">Model identifier when the provider reported one.</param>
/// <param name="PromptTokens">Input-side token count when reported.</param>
/// <param name="CompletionTokens">Output-side token count when reported.</param>
/// <param name="Duration">Wall-clock time from turn start to completion.</param>
public sealed record TurnCompleted(
    DateTimeOffset At,
    AgentContext Context,
    string AssistantText,
    string? ModelId,
    int? PromptTokens,
    int? CompletionTokens,
    TimeSpan Duration)
    : AgentEvent(At, Context);

/// <summary>
/// Emitted when a turn ends with an exception. The user turn is still in history
/// (callers typically keep it so a retry UX can resend the same message); no
/// assistant turn was appended.
/// </summary>
/// <param name="At">UTC timestamp when the event was emitted.</param>
/// <param name="Context">Ambient agent context at event-emission time.</param>
/// <param name="ErrorType">Short exception type name.</param>
/// <param name="ErrorMessage">Exception message.</param>
/// <param name="Duration">Wall-clock time from turn start to failure.</param>
public sealed record TurnFailed(
    DateTimeOffset At,
    AgentContext Context,
    string ErrorType,
    string ErrorMessage,
    TimeSpan Duration)
    : AgentEvent(At, Context);
