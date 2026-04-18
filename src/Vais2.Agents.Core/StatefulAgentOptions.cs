// Copyright (c) 2026 VAIS2 Platform contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Polly;

namespace Vais2.Agents.Core;

/// <summary>
/// Construction-time options for <see cref="StatefulAiAgent"/>. Every field is
/// optional and has a sensible default; consumers override what they care about.
/// </summary>
public sealed class StatefulAgentOptions
{
    /// <summary>
    /// Optional stable name for this agent. Surfaces in telemetry via
    /// <see cref="UsageRecord.AgentName"/>.
    /// </summary>
    public string? AgentName { get; init; }

    /// <summary>
    /// System instruction prepended to every turn. Mutable after construction via
    /// <see cref="IAiAgent.SystemPrompt"/>.
    /// </summary>
    public string? SystemPrompt { get; init; }

    /// <summary>
    /// Ordered filter chain. Filters run in the order given, outermost first.
    /// Default: empty (no filters).
    /// </summary>
    public IReadOnlyList<IAgentFilter> Filters { get; init; } = Array.Empty<IAgentFilter>();

    /// <summary>
    /// Usage telemetry sink. Default: <see cref="NullUsageSink.Instance"/>.
    /// </summary>
    public IUsageSink? UsageSink { get; init; }

    /// <summary>
    /// Ambient context accessor. Default: a private <see cref="AsyncLocalAgentContextAccessor"/>.
    /// </summary>
    public IAgentContextAccessor? ContextAccessor { get; init; }

    /// <summary>
    /// Resilience pipeline wrapping the provider call. When null, the core uses a
    /// default pipeline with 3 retries and exponential back-off — matching the
    /// behaviour of the previous <c>AiAgent&lt;T&gt;.CallFunction</c> in VAIS2.
    /// </summary>
    public ResiliencePipeline? ResiliencePipeline { get; init; }

    /// <summary>
    /// Tools made available to the model on every turn. When set, the registry's
    /// <see cref="IToolRegistry.Tools"/> is attached to each <see cref="CompletionRequest"/>
    /// and the adapter is expected to advertise them to its underlying SDK with
    /// auto-invocation enabled. Null means the agent operates without tools.
    /// </summary>
    public IToolRegistry? ToolRegistry { get; init; }

    /// <summary>
    /// Optional history to seed the agent's default session at construction. Intended for
    /// hosts that persist chat state externally and reconstruct <see cref="StatefulAiAgent"/>
    /// on activation. The supplied turns are copied into the default session's history in
    /// order; callers may safely hand in a snapshot and mutate their source afterwards.
    /// </summary>
    /// <remarks>
    /// Ignored — and rejected at construction — when <see cref="Session"/> is also supplied.
    /// The caller owns the session's state in that case.
    /// </remarks>
    public IReadOnlyList<ChatTurn>? InitialHistory { get; init; }

    /// <summary>
    /// Conversation container this agent binds to. When null (the default), the agent
    /// constructs a private <see cref="InMemoryAgentSession"/> using <see cref="AgentName"/>
    /// as the agent identifier (falling back to <c>"agent"</c>) and a fresh GUID as the
    /// session identifier. Supply a session explicitly to share state with other consumers
    /// or to bind the agent to a persistent, externally managed conversation.
    /// </summary>
    /// <remarks>
    /// When both <see cref="Session"/> and <see cref="InitialHistory"/> are set, construction
    /// throws — the session owns its history and merging the two silently would hide bugs.
    /// </remarks>
    public IAgentSession? Session { get; init; }

    /// <summary>
    /// Optional semantic-event bus. When set, <see cref="StatefulAiAgent"/> publishes
    /// <see cref="TurnStarted"/> before each provider call and <see cref="TurnCompleted"/>
    /// / <see cref="TurnFailed"/> after each turn resolves. Default: <see cref="NullAgentEventBus.Instance"/>
    /// (no-op). Bus failures are logged and swallowed; events never break the main flow.
    /// </summary>
    public IAgentEventBus? EventBus { get; init; }

    /// <summary>
    /// Optional long-term / working memory store. Exposed for consumers that wire memory-backed
    /// context providers or custom filters; <see cref="StatefulAiAgent"/> does not consult this
    /// field directly in v0.4 (it lands as a concern of the context-provider pillar). Default:
    /// <see cref="NullMemoryStore.Instance"/> (no-op).
    /// </summary>
    public IMemoryStore? MemoryStore { get; init; }

    /// <summary>
    /// Optional reducer applied to the session's history before each turn's request is built.
    /// Default: <see cref="NoopHistoryReducer.Instance"/> (identity — history passes through
    /// unchanged, preserving pre-0.4 behaviour).
    /// </summary>
    public IHistoryReducer? HistoryReducer { get; init; }

    /// <summary>
    /// Ordered per-turn context contributors. Each runs once per turn; the host merges every
    /// <see cref="ContextContribution"/> into the candidate <see cref="CompletionRequest"/>
    /// before the filter chain and the provider call. Default: empty (no providers).
    /// </summary>
    /// <remarks>
    /// Provider exceptions propagate and fail the turn; wrap providers yourself if swallow
    /// semantics are wanted. Multiple providers' <see cref="ContextContribution.SystemPromptAddendum"/>
    /// values are concatenated in provider order with <c>"\n\n"</c> separators.
    /// </remarks>
    public IReadOnlyList<IContextProvider> ContextProviders { get; init; } = Array.Empty<IContextProvider>();

    /// <summary>
    /// Runs after all <see cref="ContextProviders"/> have contributed, to fit the merged
    /// candidate into whatever window the packer cares about. Default:
    /// <see cref="NoopContextWindowPacker.Instance"/> (identity).
    /// </summary>
    public IContextWindowPacker? ContextWindowPacker { get; init; }

    /// <summary>
    /// Optional multi-part system prompt composer. When non-null, <see cref="StatefulAiAgent"/>
    /// calls <see cref="ISystemPromptComposer.ComposeAsync"/> at the start of every turn
    /// and uses the returned string as the base system prompt — the plain
    /// <see cref="SystemPrompt"/> string is ignored in that case. Context-provider
    /// <see cref="ContextContribution.SystemPromptAddendum"/> values still concatenate
    /// on top of the composed base.
    /// </summary>
    public ISystemPromptComposer? SystemPromptComposer { get; init; }

    /// <summary>
    /// Ordered input guardrails. Run on the fully-prepared <see cref="CompletionRequest"/>
    /// before any filter or provider call; first <see cref="GuardrailDecision.Deny"/> short-circuits
    /// the turn with an <see cref="AgentGuardrailDeniedException"/>. Default: empty.
    /// </summary>
    public IReadOnlyList<IInputGuardrail> InputGuardrails { get; init; } = Array.Empty<IInputGuardrail>();

    /// <summary>
    /// Ordered output guardrails. Run on the <see cref="CompletionResponse"/> after the provider
    /// returns, before the assistant turn is appended to the session; first
    /// <see cref="GuardrailDecision.Deny"/> short-circuits with an
    /// <see cref="AgentGuardrailDeniedException"/> (assistant turn is NOT appended). Default: empty.
    /// In streaming turns, output guardrails run after the accumulator drains (post-facto).
    /// </summary>
    public IReadOnlyList<IOutputGuardrail> OutputGuardrails { get; init; } = Array.Empty<IOutputGuardrail>();

    /// <summary>
    /// Ordered tool guardrails. <b>Not wired in v0.4</b> — exposed for consumers to start writing
    /// implementations against; the per-tool-call seam lands with the execution-loop pillar
    /// (§9.5) when <c>IToolCallDispatcher</c> introduces a host-side invocation hook.
    /// Default: empty.
    /// </summary>
    public IReadOnlyList<IToolGuardrail> ToolGuardrails { get; init; } = Array.Empty<IToolGuardrail>();

    /// <summary>
    /// Per-run caps (turns, tool calls, tokens, duration). Ships in v0.4 PR 8 for consumers to
    /// wire now; enforcement lands in PR 9 when <c>StatefulAiAgent</c> takes over the outer
    /// tool-call loop and "a run" becomes well-defined. Default: null (unlimited).
    /// </summary>
    public RunBudget? Budget { get; init; }

    /// <summary>
    /// Ordered pipeline of streaming filters applied by <c>StatefulAiAgent.StreamAsync</c>.
    /// Each <see cref="CompletionUpdate"/> flowing from the provider passes through the chain
    /// in order before being yielded to the caller; <see cref="IStreamingAgentFilter.OnStreamCompleteAsync"/>
    /// fires on every filter after the accumulator drains. Default: empty.
    /// </summary>
    public IReadOnlyList<IStreamingAgentFilter> StreamingFilters { get; init; } = Array.Empty<IStreamingAgentFilter>();

    /// <summary>
    /// Dispatcher for tool calls surfaced by the provider. When null, <c>StatefulAiAgent</c>
    /// constructs a <see cref="DefaultToolCallDispatcher"/> from <see cref="ToolRegistry"/> +
    /// <see cref="ToolGuardrails"/> automatically. Supply your own to override — e.g., a
    /// journaled dispatcher for durable execution.
    /// </summary>
    public IToolCallDispatcher? ToolCallDispatcher { get; init; }
}
