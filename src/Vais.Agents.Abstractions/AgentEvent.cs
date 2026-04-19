// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents;

/// <summary>
/// Base type for semantic events emitted by an <see cref="IAiAgent"/> as it processes
/// turns. Complement to <see cref="IUsageSink"/>: usage sink is numeric telemetry
/// (tokens, duration, success/fail) for aggregation; agent events carry the semantic
/// payload (user message, assistant text, error) for event-driven consumers
/// (UIs, integrations, audit logs, cross-process fan-out via Redis streams).
/// </summary>
/// <remarks>
/// <para>
/// Closed hierarchy: the Abstractions package defines exactly these six concrete
/// shapes. Consumers pattern-match on subtype; adding a new subtype is an
/// <em>unshipped</em> addition to Abstractions, not a subclass in downstream code.
/// Keeps wire serialisation (Orleans, Redis streams) deterministic.
/// </para>
/// <para>
/// Tool-invocation and guardrail events (<see cref="ToolCallStarted"/>,
/// <see cref="ToolCallCompleted"/>, <see cref="GuardrailTriggered"/>) landed in
/// v0.4 once <c>StatefulAiAgent</c> took over the outer tool-call loop.
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

/// <summary>
/// Emitted by <c>DefaultToolCallDispatcher</c> immediately before a tool's
/// <see cref="ITool.InvokeAsync"/> is called. Paired 1:1 with a subsequent
/// <see cref="ToolCallCompleted"/>.
/// </summary>
/// <param name="At">UTC timestamp when the event was emitted.</param>
/// <param name="Context">Ambient agent context at event-emission time.</param>
/// <param name="CallId">Provider-assigned correlation id for this tool call.</param>
/// <param name="ToolName">Name of the tool about to be invoked.</param>
public sealed record ToolCallStarted(
    DateTimeOffset At,
    AgentContext Context,
    string CallId,
    string ToolName)
    : AgentEvent(At, Context);

/// <summary>
/// Emitted by <c>DefaultToolCallDispatcher</c> after a tool invocation resolves —
/// either successfully, as a tool-exception-captured outcome (<see cref="Succeeded"/>
/// false, <see cref="Error"/> set), or after an <c>AfterInvokeAsync</c> guardrail
/// denial.
/// </summary>
/// <param name="At">UTC timestamp when the event was emitted.</param>
/// <param name="Context">Ambient agent context at event-emission time.</param>
/// <param name="CallId">Correlation id matching <see cref="ToolCallStarted.CallId"/>.</param>
/// <param name="ToolName">Name of the tool that was invoked.</param>
/// <param name="Succeeded">True when the tool returned normally; false when it threw.</param>
/// <param name="Error">Tool exception type name when <see cref="Succeeded"/> is false; null on success.</param>
/// <param name="Duration">Wall-clock duration of the tool invocation.</param>
public sealed record ToolCallCompleted(
    DateTimeOffset At,
    AgentContext Context,
    string CallId,
    string ToolName,
    bool Succeeded,
    string? Error,
    TimeSpan Duration)
    : AgentEvent(At, Context);

/// <summary>
/// Emitted when a guardrail returns <see cref="GuardrailDecision.Deny"/> at any
/// of the three middleware layers. Fires once per denial, immediately before
/// <see cref="AgentGuardrailDeniedException"/> propagates up and
/// <see cref="TurnFailed"/> is emitted for the whole turn.
/// </summary>
/// <param name="At">UTC timestamp when the event was emitted.</param>
/// <param name="Context">Ambient agent context at event-emission time.</param>
/// <param name="Layer">Which middleware layer raised the denial.</param>
/// <param name="Decision">The decision the guardrail returned (currently always <see cref="GuardrailDecision.Deny"/>).</param>
/// <param name="Reason">The operator-readable reason the guardrail supplied, if any.</param>
public sealed record GuardrailTriggered(
    DateTimeOffset At,
    AgentContext Context,
    GuardrailLayer Layer,
    GuardrailDecision Decision,
    string? Reason)
    : AgentEvent(At, Context);

/// <summary>
/// Emitted when a guardrail or tool dispatcher raises an <see cref="AgentInterrupt"/>.
/// Paired with a subsequent <see cref="TurnFailed"/>; callers typically react by
/// gathering human input and invoking the agent's resume entry point with a
/// <see cref="ResumeInput"/> carrying the same <see cref="InterruptId"/>.
/// </summary>
/// <param name="At">UTC timestamp when the event was emitted.</param>
/// <param name="Context">Ambient agent context at event-emission time.</param>
/// <param name="InterruptId">Correlation id carried into the matching <see cref="ResumeInput.InterruptId"/>.</param>
/// <param name="Reason">Operator-readable reason the guardrail/dispatcher supplied.</param>
public sealed record InterruptRaised(
    DateTimeOffset At,
    AgentContext Context,
    string InterruptId,
    string Reason)
    : AgentEvent(At, Context);

/// <summary>
/// Emitted by a multi-agent orchestrator when a participant or the orchestrator
/// itself signals a handoff from one agent to another. Carries the <see cref="Handoff"/>
/// payload describing the transition; observers use it for audit trails, UI
/// updates, or to drive cross-agent telemetry correlation.
/// </summary>
/// <param name="At">UTC timestamp when the event was emitted.</param>
/// <param name="Context">Ambient agent context at event-emission time.</param>
/// <param name="Handoff">The handoff payload — source, target, optional message, optional history-to-carry.</param>
public sealed record HandoffRequested(
    DateTimeOffset At,
    AgentContext Context,
    Handoff Handoff)
    : AgentEvent(At, Context);

/// <summary>
/// Emitted by the tool-call dispatcher when a cache hit on the durable-execution
/// journal short-circuits a tool invocation. Paired semantically with the
/// originating <see cref="ToolCallCompleted"/> from the first dispatch in the
/// run; the replay itself doesn't re-emit <see cref="ToolCallStarted"/> to avoid
/// double-counting in observability backends.
/// </summary>
/// <remarks>
/// Only fires when the dispatcher has a real <see cref="IAgentJournal"/> wired
/// and <see cref="AgentContext.RunId"/> is set; consumers that haven't opted
/// into the durable-execution pillar will never see this event.
/// </remarks>
/// <param name="At">UTC timestamp when the replay was served.</param>
/// <param name="Context">Ambient agent context at replay time.</param>
/// <param name="CallId">Correlation id of the originally-dispatched tool call.</param>
/// <param name="ToolName">Name of the tool whose outcome was replayed from the journal.</param>
public sealed record ToolCallReplayed(
    DateTimeOffset At,
    AgentContext Context,
    string CallId,
    string ToolName)
    : AgentEvent(At, Context);
