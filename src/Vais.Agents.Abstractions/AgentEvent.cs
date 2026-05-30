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
/// Closed hierarchy: the Abstractions package defines exactly these fourteen concrete
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
    : AgentEvent(At, Context)
{
    /// <summary>
    /// Severity of this tool outcome. A tool error that the dispatcher captures and feeds
    /// back to the model is a <em>recovered</em> failure (<see cref="FailureLevel.Warning"/>),
    /// not a turn-fatal one — so it stays visible in the run-health rollup without looking
    /// like a hard error. Defaults to <see cref="FailureLevel.Default"/> on success; the
    /// positional constructor is unchanged so existing callers are unaffected.
    /// </summary>
    public FailureLevel Level { get; init; } = FailureLevel.Default;
}

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

/// <summary>
/// Emitted by <see cref="IStreamingAiAgent.StreamAsync"/> per streamed text chunk —
/// the event-stream analogue of <see cref="CompletionUpdate"/>. Mirrors the same
/// five fields so the streaming-invoke wire can carry text deltas + metadata
/// (ModelId, token counts, terminal tool-calls) without introducing a second
/// shape. Consumers who want "just text" filter the event stream to this type
/// and read <see cref="TextDelta"/>.
/// </summary>
/// <remarks>
/// <para>
/// v0.12 addition. <see cref="TextDelta"/> is always non-null; may be empty on
/// a terminal update that carries only metadata or tool-calls. Consumers
/// aggregating a run should sum deltas and take the final non-null
/// <see cref="ModelId"/> / <see cref="PromptTokens"/> /
/// <see cref="CompletionTokens"/> as authoritative — same rule as
/// <see cref="CompletionUpdate"/>.
/// </para>
/// <para>
/// <see cref="ToolCalls"/> is populated on terminal pre-dispatch updates when
/// the model requests tool invocations. Inside the default agent's tool-call
/// loop, <c>ToolCalls</c> fires just before the dispatcher runs and
/// surfaces each tool call's payload to observing consumers — actual dispatch
/// events (<see cref="ToolCallStarted"/> / <see cref="ToolCallCompleted"/>)
/// follow separately.
/// </para>
/// </remarks>
/// <param name="At">UTC timestamp when the chunk was emitted.</param>
/// <param name="Context">Ambient agent context at emission time.</param>
/// <param name="TextDelta">Next fragment of assistant text. Non-null; may be empty.</param>
/// <param name="ModelId">Model id, typically populated on the final update only.</param>
/// <param name="PromptTokens">Prompt-side token count, typically populated on the final update only.</param>
/// <param name="CompletionTokens">Completion-side token count, typically populated on the final update only.</param>
/// <param name="ToolCalls">Tool calls the model requested on this turn, populated on the terminal pre-dispatch update only.</param>
public sealed record CompletionDelta(
    DateTimeOffset At,
    AgentContext Context,
    string TextDelta,
    string? ModelId = null,
    int? PromptTokens = null,
    int? CompletionTokens = null,
    IReadOnlyList<ToolCallRequest>? ToolCalls = null)
    : AgentEvent(At, Context);

/// <summary>
/// Emitted once per turn by <c>SectionTelemetryEmitter</c> (when at least one sink is wired)
/// after the section pipeline runs the resolver and packer, before the flattener. Carries the
/// per-section breakdown that drives downstream observability surfaces (Prometheus dashboards,
/// Langfuse trace metadata, custom audit / eval pipelines).
/// </summary>
/// <remarks>
/// <para>
/// Consumers typically subscribe to assert invariants ("every successful run includes a
/// <c>cognition.diee.goal_stack</c> section"), fail evals on missing producers, or feed a
/// drift detector that watches section ratios over time. The event fires <em>before</em>
/// guardrails and the completion provider, so even cancelled or guardrail-denied turns emit it.
/// </para>
/// <para>
/// Subscribers must not mutate <paramref name="Sections"/> or <paramref name="Budget"/>;
/// the records are immutable but the lists are <see cref="IReadOnlyList{T}"/> — treat them as
/// snapshots.
/// </para>
/// </remarks>
/// <param name="At">UTC timestamp when the snapshot was built.</param>
/// <param name="Context">Ambient agent context at emission time.</param>
/// <param name="TurnIndex">1-based turn index within the run (turn 1 is the first model call; tool-call loops increment).</param>
/// <param name="Sections">One measurement per section that entered the packer, in input order. Surviving and dropped sections both appear; the outcome field distinguishes them.</param>
/// <param name="Budget">Aggregate counters: budget target, used, dropped, truncated.</param>
public sealed record RequestSectionsBuilt(
    DateTimeOffset At,
    AgentContext Context,
    int TurnIndex,
    IReadOnlyList<SectionMeasurement> Sections,
    SectionBudgetSummary Budget)
    : AgentEvent(At, Context);

/// <summary>
/// Emitted by the eval runner grain as cases complete so SSE subscribers receive
/// real-time progress. One event per case completion plus a terminal
/// <c>run-completed</c> event when all cases finish.
/// </summary>
/// <param name="At">UTC timestamp when the event was emitted.</param>
/// <param name="Context">Ambient context at emission time.</param>
/// <param name="EvalRunId">The eval run that produced this progress event.</param>
/// <param name="ProgressKind">
/// Discriminator: <c>case-started</c>, <c>case-completed</c>, <c>run-completed</c>.
/// </param>
/// <param name="CaseId">Case identifier — non-null for <c>case-started</c> and <c>case-completed</c>.</param>
/// <param name="CaseStatus">
/// Case result status — non-null for <c>case-completed</c>.
/// </param>
public sealed record EvalRunProgress(
    DateTimeOffset At,
    AgentContext Context,
    string EvalRunId,
    string ProgressKind,
    string? CaseId = null,
    int? CaseStatus = null)
    : AgentEvent(At, Context);

/// <summary>
/// Emitted when an LLM call fails and is retried by the agent's resilience pipeline.
/// One event per <em>failed</em> attempt that triggers a retry; the final successful
/// attempt (or the terminal failure → <see cref="TurnFailed"/>) is not a retry event.
/// </summary>
/// <remarks>
/// A recovered retry (a later attempt succeeds) is a degraded-but-not-fatal signal, so
/// <see cref="Level"/> is <see cref="FailureLevel.Warning"/>. Without this event a silent
/// retry loop is invisible — "answered, but only after 2 retries" cannot be distinguished
/// from a clean first-attempt success. Feeds the run-health rollup and eval mechanical axis.
/// </remarks>
/// <param name="At">UTC timestamp when the retry was scheduled.</param>
/// <param name="Context">Ambient agent context at emission time.</param>
/// <param name="AttemptIndex">Zero-based index of the attempt that just failed (0 = the first attempt). Matches <c>vais.stream.attempt.index</c> semantics.</param>
/// <param name="ErrorType">Short exception type name of the failure that triggered the retry.</param>
/// <param name="IsTransient">True when the failure was classified transient (retry-eligible); see <see cref="IClassifiedAgentError"/>.</param>
/// <param name="Level">Severity — <see cref="FailureLevel.Warning"/> for a recovered retry.</param>
public sealed record LlmCallRetried(
    DateTimeOffset At,
    AgentContext Context,
    int AttemptIndex,
    string ErrorType,
    bool IsTransient,
    FailureLevel Level = FailureLevel.Warning)
    : AgentEvent(At, Context);

/// <summary>
/// Emitted when the LLM fallback middleware abandons one provider and tries the next in
/// the pool. One event per fall-through; a successful fallback therefore leaves a trail of
/// "primary failed → answered on the backup" that is otherwise completely invisible.
/// </summary>
/// <remarks>
/// Because <see cref="ICompletionProvider"/> is opaque (it exposes no model id), provider
/// identity is reported as the ordered pool index plus the runtime type name —
/// <em>not</em> a model id. A model-level "gpt-4o → gpt-4o-mini" attribution would require
/// the provider to surface its model; that is a separate follow-up. <see cref="Level"/> is
/// <see cref="FailureLevel.Warning"/> (the call still recovers); only an all-providers-exhausted
/// failure becomes a <see cref="TurnFailed"/> error.
/// </remarks>
/// <param name="At">UTC timestamp when the fallback was engaged.</param>
/// <param name="Context">Ambient agent context at emission time.</param>
/// <param name="FromProviderIndex">Zero-based pool index of the provider that just failed.</param>
/// <param name="ToProviderIndex">Zero-based pool index of the provider being tried next.</param>
/// <param name="FromProviderType">Runtime type name of the failed provider, when available.</param>
/// <param name="ToProviderType">Runtime type name of the next provider, when available.</param>
/// <param name="Reason">Short exception type name of the failure that triggered the fallback.</param>
/// <param name="Level">Severity — <see cref="FailureLevel.Warning"/> for an engaged (recovering) fallback.</param>
public sealed record LlmFallbackEngaged(
    DateTimeOffset At,
    AgentContext Context,
    int FromProviderIndex,
    int ToProviderIndex,
    string? FromProviderType,
    string? ToProviderType,
    string Reason,
    FailureLevel Level = FailureLevel.Warning)
    : AgentEvent(At, Context);
