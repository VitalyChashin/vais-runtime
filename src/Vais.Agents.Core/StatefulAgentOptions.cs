// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Polly;

namespace Vais.Agents.Core;

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
    /// Optional per-agent completion provider. When set, the hosting layer
    /// (e.g. <c>AiAgentGrain</c>) uses this instance instead of whatever
    /// <see cref="ICompletionProvider"/> is registered as the ambient DI
    /// singleton. Set by the v0.17 manifest translator so each agent id can
    /// use a different model provider derived from its manifest's
    /// <c>ModelSpec</c>. Default: null (host falls back to DI-registered
    /// provider).
    /// </summary>
    public ICompletionProvider? CompletionProvider { get; init; }

    /// <summary>
    /// Optional pre-constructed agent instance supplied by the manifest
    /// instantiation pipeline (v0.18 Pillar C — plugin model). When set,
    /// host-side grain activation uses this instance verbatim instead of
    /// constructing <see cref="StatefulAiAgent"/> from the declarative
    /// slots. Null ⇒ fall through to the v0.17 declarative path.
    /// </summary>
    /// <remarks>
    /// Populated by the translator when <c>AgentManifest.Handler.TypeName</c>
    /// matches a loaded plugin's <c>IAgentHandlerFactory.HandlerTypeName</c>.
    /// Plugin factories that want the standard execution loop return a
    /// <c>new StatefulAiAgent(provider, options)</c>; plugins that own
    /// their loop implement <see cref="IAiAgent"/> directly.
    /// </remarks>
    public IAiAgent? Agent { get; init; }

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
    /// Resilience pipeline wrapping the provider call on the non-streaming path
    /// (<c>StatefulAiAgent.AskAsync</c>). When null, the core uses a default
    /// pipeline with 3 retries and exponential back-off — matching the behaviour
    /// of the previous <c>AiAgent&lt;T&gt;.CallFunction</c> in earlier SK-based
    /// deployments. Filter-domain exceptions
    /// (<see cref="AgentGuardrailDeniedException"/>,
    /// <see cref="AgentBudgetExceededException"/>,
    /// <see cref="AgentInterruptedException"/>,
    /// <see cref="OperationCanceledException"/>) are excluded from retry.
    /// </summary>
    public ResiliencePipeline? ResiliencePipeline { get; init; }

    /// <summary>
    /// Resilience pipeline wrapping the streaming provider call on
    /// <c>StatefulAiAgent.StreamAsync</c>. When null, the core uses an internal
    /// default pipeline with the same retry cadence as
    /// <see cref="ResiliencePipeline"/> but narrowed to only apply before the
    /// first <see cref="CompletionUpdate"/> is yielded — once the stream is
    /// producing deltas, yielded content is considered committed and retries stop.
    /// Filter-domain exceptions are excluded from retry on the same terms as the
    /// non-streaming pipeline. Consumers who want identical retry budgets on both
    /// paths assign the same <see cref="Polly.ResiliencePipeline"/> instance to
    /// both properties; consumers who want separate budgets assign two.
    /// </summary>
    public ResiliencePipeline? StreamingResiliencePipeline { get; init; }

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
    /// Ordered gateway middleware applied as the outermost layer of both the
    /// non-streaming filter chain and the streaming filter chain. Each
    /// <see cref="LlmGatewayMiddleware"/> instance covers both paths; register via
    /// <c>services.AddLlmGatewayMiddleware&lt;T&gt;()</c> for DI-driven injection,
    /// or set here directly for manual construction. Gateway middleware runs before
    /// any <see cref="Filters"/> / <see cref="StreamingFilters"/>. Default: empty.
    /// </summary>
    public IReadOnlyList<LlmGatewayMiddleware> GatewayMiddleware { get; init; }
        = Array.Empty<LlmGatewayMiddleware>();

    /// <summary>
    /// Dispatcher for tool calls surfaced by the provider. When null, <c>StatefulAiAgent</c>
    /// constructs a <see cref="DefaultToolCallDispatcher"/> from <see cref="ToolRegistry"/> +
    /// <see cref="ToolGuardrails"/> automatically. Supply your own to override — e.g., a
    /// journaled dispatcher for durable execution.
    /// </summary>
    public IToolCallDispatcher? ToolCallDispatcher { get; init; }

    /// <summary>
    /// Durable-execution journal. Every tool-call outcome is appended here when
    /// <see cref="AgentContext.RunId"/> is stamped; re-dispatching the same call
    /// within the same run returns the recorded outcome without re-invoking the
    /// tool. Default: <see cref="NullAgentJournal.Instance"/> (no-op — preserves
    /// pre-0.5 behaviour).
    /// </summary>
    public IAgentJournal? Journal { get; init; }

    /// <summary>
    /// Controls the granularity of replay when a streaming run is resumed via
    /// <see cref="Journal"/>. The default <c>ToolOnly</c> mode replays only
    /// tool-call outcomes and re-invokes the provider for fresh deltas.
    /// <c>Full</c> mode replays both tool-call outcomes and completion
    /// deltas, bypassing the provider entirely.
    /// </summary>
    /// <remarks>
    /// When <c>Full</c> mode is enabled, each <see cref="CompletionUpdate"/>
    /// yielded during streaming is journaled as a <see cref="CompletionDeltaRecorded"/>
    /// entry. On resume, the exact delta sequence is re-yielded verbatim, enabling
    /// deterministic delta-by-delta reproduction at the cost of additional journal
    /// storage.
    /// </remarks>
    public ReplayMode ReplayMode { get; init; } = Vais.Agents.ReplayMode.ToolOnly;

    /// <summary>
    /// Factory that produces the <see cref="AgentContext.RunId"/> stamped on every
    /// <c>AskAsync</c> / <c>StreamAsync</c> invocation. Default is
    /// <c>Guid.NewGuid().ToString("N")</c> — a collision-free 32-hex identifier.
    /// Override for structured ids (ULID, KSUID, Snowflake) when downstream systems
    /// need time-sortable or otherwise structured run identifiers.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The factory is consulted only when the caller hasn't already stamped a RunId
    /// on the ambient <see cref="IAgentContextAccessor"/>. A non-null ambient
    /// <see cref="AgentContext.RunId"/> always wins — this is how resume threads
    /// the interrupted run's id back into the continuation (see v0.5 PR 4).
    /// </para>
    /// </remarks>
    public Func<string>? RunIdFactory { get; init; }
}
