// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Polly;
using Polly.Retry;

namespace Vais.Agents.Core;

/// <summary>
/// Default, in-process <see cref="IAiAgent"/> implementation. Owns its own chat
/// history and runs each turn through:
/// <list type="number">
///   <item>a resilience pipeline (default: 3 retries, exponential back-off),</item>
///   <item>an ordered filter chain,</item>
///   <item>the injected <see cref="ICompletionProvider"/>,</item>
///   <item>a usage sink.</item>
/// </list>
/// Stack-neutral: swapping Semantic Kernel for Microsoft Agent Framework is purely
/// a DI change — this class does not know which backend answered.
/// </summary>
/// <remarks>
/// Not thread-safe: concurrent calls into <see cref="AskAsync"/> on one instance
/// race on the history list. Agents are typically addressed by stable identifiers
/// (e.g. Orleans grain keys) at a higher layer that serialises calls per instance.
/// </remarks>
public sealed class StatefulAiAgent : IAiAgent, IStreamingAiAgent
{
    private static readonly ResiliencePipeline _defaultPipeline = BuildDefaultPipeline();
    private static readonly ResiliencePipeline _defaultStreamingPipeline = BuildDefaultStreamingPipeline();

    private readonly ICompletionProvider _provider;
    private readonly ILogger<StatefulAiAgent> _logger;
    private readonly IAgentSession _session;
    private readonly IReadOnlyList<IAgentFilter> _filters;
    private readonly IUsageSink _usageSink;
    private readonly IAgentEventBus _eventBus;
    private readonly IAgentContextAccessor _contextAccessor;
    private readonly ResiliencePipeline _pipeline;
    private readonly ResiliencePipeline _streamingPipeline;
    private readonly IToolRegistry? _toolRegistry;
    private readonly IHistoryReducer _historyReducer;
    private readonly IReadOnlyList<IContextProvider> _contextProviders;
    private readonly ISectionResolver _sectionResolver;
    private readonly ISectionWindowPacker _sectionWindowPacker;
    private readonly SectionBudgetContext _sectionBudget;
    private readonly SectionTelemetryEmitter _sectionTelemetryEmitter;
    private readonly ISystemPromptComposer? _systemPromptComposer;
    private readonly IReadOnlyList<IInputGuardrail> _inputGuardrails;
    private readonly IReadOnlyList<IOutputGuardrail> _outputGuardrails;
    private readonly IReadOnlyList<IStreamingAgentFilter> _streamingFilters;
    private readonly AgentOutputMiddleware[] _outputMiddleware;
    private readonly IReadOnlyList<ErrorInterceptor> _errorInterceptors;
    private readonly RunBudget _budget;
    private readonly IToolCallDispatcher _toolCallDispatcher;
    private readonly IAgentJournal _journal;
    private readonly ReplayMode _replayMode;
    private readonly Func<string> _runIdFactory;
    private readonly string? _agentName;
    private readonly ResponseFormatSpec? _responseFormat;

    /// <summary>
    /// Create a new agent bound to a completion provider. All cross-cutting
    /// behaviours default to no-ops; override via <paramref name="options"/>.
    /// </summary>
    /// <param name="provider">The provider that executes each completion turn.</param>
    /// <param name="options">Optional overrides (filters, usage sink, resilience, system prompt, agent name).</param>
    /// <param name="logger">Optional logger. A null-logger is used if none is supplied.</param>
    /// <exception cref="ArgumentNullException"><paramref name="provider"/> is null.</exception>
    public StatefulAiAgent(
        ICompletionProvider provider,
        StatefulAgentOptions? options = null,
        ILogger<StatefulAiAgent>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _provider = provider;
        _logger = logger ?? NullLogger<StatefulAiAgent>.Instance;

        options ??= new StatefulAgentOptions();

        if (options.Session is not null && options.InitialHistory is { Count: > 0 })
        {
            throw new ArgumentException(
                $"{nameof(StatefulAgentOptions)}: cannot set both {nameof(StatefulAgentOptions.Session)} and {nameof(StatefulAgentOptions.InitialHistory)}. " +
                "When Session is supplied it owns the history; seed the session directly and leave InitialHistory null.",
                nameof(options));
        }

        var gw = options.GatewayMiddleware;
        _filters = gw.Count == 0
            ? options.Filters
            : [.. gw, .. options.Filters];
        _usageSink = options.UsageSink ?? NullUsageSink.Instance;
        _eventBus = options.EventBus ?? NullAgentEventBus.Instance;
        _contextAccessor = options.ContextAccessor ?? new AsyncLocalAgentContextAccessor();
        // When a real event bus is wired, build per-instance pipelines whose Polly OnRetry hook
        // publishes LlmCallRetried — so a recovered retry loop is observable (parity with the
        // streaming path's per-attempt spans). With no bus we keep the shared static pipelines
        // (zero-alloc default). A caller-supplied pipeline is honoured verbatim.
        var emitRetryEvents = options.EventBus is not null;
        _pipeline = options.ResiliencePipeline
            ?? (emitRetryEvents ? BuildRetryEventPipeline() : _defaultPipeline);
        _streamingPipeline = options.StreamingResiliencePipeline ?? _defaultStreamingPipeline;
        _toolRegistry = options.ToolRegistry;
        _historyReducer = options.HistoryReducer ?? NoopHistoryReducer.Instance;
        _contextProviders = options.ContextProviders;
        _sectionResolver = options.SectionResolver ?? DefaultSectionResolver.Instance;
        _sectionWindowPacker = options.SectionWindowPacker
            ?? (options.ContextWindowPacker is not null
                ? new LegacyPackerAdapter(options.ContextWindowPacker)
                : DefaultSectionWindowPacker.Instance);
        _sectionBudget = options.SectionBudget ?? SectionBudgetContext.Unlimited;
        _sectionTelemetryEmitter = options.SectionTelemetrySinks.Count == 0
            ? SectionTelemetryEmitter.NoOp
            : new SectionTelemetryEmitter(options.SectionTelemetrySinks, _logger);
        _systemPromptComposer = options.SystemPromptComposer;
        _inputGuardrails = options.InputGuardrails;
        _outputGuardrails = options.OutputGuardrails;
        _streamingFilters = gw.Count == 0
            ? options.StreamingFilters
            : [.. gw, .. options.StreamingFilters];
        _budget = options.Budget ?? RunBudget.Unlimited;
        _toolCallDispatcher = options.ToolCallDispatcher
            ?? new DefaultToolCallDispatcher(
                options.ToolRegistry,
                options.ToolGuardrails,
                _eventBus,
                options.Journal,
                options.ToolGatewayMiddleware.Count > 0 ? options.ToolGatewayMiddleware : null,
                _contextAccessor as IAgentContextSetter);
        _journal = options.Journal ?? NullAgentJournal.Instance;
        _replayMode = options.ReplayMode;
        _runIdFactory = options.RunIdFactory ?? DefaultRunIdFactory;
        _agentName = options.AgentName;
        _outputMiddleware = options.OutputMiddleware.Count == 0
            ? []
            : options.OutputMiddleware.ToArray();
        _errorInterceptors = options.ErrorInterceptors;
        _session = options.Session ?? new InMemoryAgentSession(
            agentId: _agentName ?? "agent",
            sessionId: null,
            initialHistory: options.InitialHistory);

        SystemPrompt = options.SystemPrompt;
        _responseFormat = options.ResponseFormat;
    }

    /// <inheritdoc />
    public string? SystemPrompt { get; set; }

    /// <inheritdoc />
    public IAgentSession Session => _session;

    /// <inheritdoc />
    public IReadOnlyList<ChatTurn> History => _session.History;

    /// <inheritdoc />
    public Task<string> AskAsync(string userMessage, CancellationToken cancellationToken = default)
        => AskAsyncCore(userMessage, runIdOverride: null, cancellationToken);

    /// <summary>
    /// Alias for <see cref="AskAsync"/>. Matches the <c>InvokeAsync</c> naming convention
    /// used by <see cref="IAgentGraph{TState}"/> and the A2A/MCP server invoke paths.
    /// </summary>
#pragma warning disable RS0026 // Two overloads with optional CT — first params are distinct types (string vs AgentInvocationRequest), not a real ambiguity
    public Task<string> InvokeAsync(string userMessage, CancellationToken cancellationToken = default)
        => AskAsyncCore(userMessage, runIdOverride: null, cancellationToken);
#pragma warning restore RS0026

    /// <summary>
    /// Invokes the agent from an <see cref="AgentInvocationRequest"/>. When
    /// <see cref="AgentInvocationRequest.InitialHistory"/> is non-empty the session is
    /// reset and seeded with those turns before processing <see cref="AgentInvocationRequest.Text"/>;
    /// this enables stateless multi-turn usage (OpenAI-compat path, edit/regenerate).
    /// When <see cref="AgentInvocationRequest.InitialHistory"/> is null the current
    /// session history is preserved unchanged.
    /// </summary>
#pragma warning disable RS0026
    public async Task<string> InvokeAsync(AgentInvocationRequest request, CancellationToken cancellationToken = default)
#pragma warning restore RS0026
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.InitialHistory is { Count: > 0 })
        {
            await _session.ResetAsync(cancellationToken).ConfigureAwait(false);
            foreach (var (role, content) in request.InitialHistory)
            {
                var chatRole = string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase)
                    ? AgentChatRole.Assistant
                    : AgentChatRole.User;
                await _session.AppendAsync(new ChatTurn(chatRole, content), cancellationToken).ConfigureAwait(false);
            }
        }

        return await AskAsyncCore(request.Text, runIdOverride: null, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> AskAsyncCore(string userMessage, string? runIdOverride, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(userMessage))
        {
            throw new ArgumentException("User message must be non-empty.", nameof(userMessage));
        }

        await _session.AppendAsync(new ChatTurn(AgentChatRole.User, userMessage), cancellationToken).ConfigureAwait(false);

        var context = StampRunId(_contextAccessor.Current, runIdOverride);
        var eventContext = BuildEventContext(context);
        var runStartedAt = DateTimeOffset.UtcNow;
        var runStopwatch = Stopwatch.StartNew();

        using var activity = StartTurnActivity(context);
        activity?.SetTag("gen_ai.prompt", userMessage);

        await PublishEventAsync(new TurnStarted(runStartedAt, eventContext, userMessage), cancellationToken).ConfigureAwait(false);

        // Working history lives for the duration of this run. It starts from the session
        // snapshot (which includes the just-appended user turn) and grows with the
        // assistant-with-tool-calls + tool-result turns produced by each loop round.
        // Session history stays clean — only user + final assistant turns land there.
        var workingHistory = new List<ChatTurn>(_session.History);

        var aggregatedPromptTokens = 0;
        var aggregatedCompletionTokens = 0;
        string? finalModelId = null;
        var totalToolCalls = 0;
        var turnIndex = 0;
        CompletionResponse? lastResponse = null;
        Exception? failure = null;

        try
        {
            while (true)
            {
                turnIndex++;
                if (_budget.MaxTurns is int maxTurns && turnIndex > maxTurns)
                {
                    throw new AgentBudgetExceededException(nameof(RunBudget.MaxTurns), maxTurns, turnIndex);
                }
                if (_budget.MaxDuration is TimeSpan maxDuration && runStopwatch.Elapsed > maxDuration)
                {
                    throw new AgentBudgetExceededException(nameof(RunBudget.MaxDuration), maxDuration, runStopwatch.Elapsed);
                }

                // Section pipeline: composer + providers emit Section[]; resolver orders them;
                // packer applies the budget; flattener produces the wire-shaped CompletionRequest.
                // Runs each round so providers can react to tool results landing in the working
                // history between rounds.
                var candidate = await BuildPerTurnRequestAsync(workingHistory, context, turnIndex, cancellationToken).ConfigureAwait(false);

                // Input guardrails fire on every model invocation — tool-call loops
                // should be able to block a mid-run escalation, not just the first turn.
                await RunInputGuardrailsAsync(candidate, context, cancellationToken).ConfigureAwait(false);

                var response = await _pipeline.ExecuteAsync(
                    async ct => await InvokeThroughFiltersAsync(candidate, ct).ConfigureAwait(false),
                    cancellationToken).ConfigureAwait(false);
                lastResponse = response;

                // Output middleware fires per LLM call (per OQ-5). Runs before the
                // tool-call loop decision so multi-round turns fire it on every round-trip.
                if (_outputMiddleware.Length > 0)
                {
                    var responseTurn = new ChatTurn(AgentChatRole.Assistant, response.Text ?? string.Empty, ToolCalls: response.ToolCalls);
                    var outputCtx = new AgentOutputContext
                    {
                        AgentId = _session.AgentId,
                        RunId = context.RunId ?? string.Empty,
                        SessionId = _session.SessionId,
                        RequestMessages = workingHistory,
                        ResponseMessage = responseTurn,
                        Usage = new TokenUsage(response.PromptTokens, response.CompletionTokens),
                    };
                    await RunOutputMiddlewareAsync(outputCtx, _outputMiddleware, cancellationToken).ConfigureAwait(false);
                }

                if (response.PromptTokens is int pt)
                {
                    aggregatedPromptTokens += pt;
                }
                if (response.CompletionTokens is int ct2)
                {
                    aggregatedCompletionTokens += ct2;
                }
                if (response.ModelId is not null)
                {
                    finalModelId = response.ModelId;
                }

                if (_budget.MaxPromptTokens is int maxPrompt && aggregatedPromptTokens > maxPrompt)
                {
                    throw new AgentBudgetExceededException(nameof(RunBudget.MaxPromptTokens), maxPrompt, aggregatedPromptTokens);
                }
                if (_budget.MaxCompletionTokens is int maxCompletion && aggregatedCompletionTokens > maxCompletion)
                {
                    throw new AgentBudgetExceededException(nameof(RunBudget.MaxCompletionTokens), maxCompletion, aggregatedCompletionTokens);
                }

                // Final-answer case: no tool calls requested. Run output guardrails
                // and fall through to the success path below.
                if (response.ToolCalls is null || response.ToolCalls.Count == 0)
                {
                    await RunOutputGuardrailsAsync(response, context, cancellationToken).ConfigureAwait(false);
                    break;
                }

                // Tool-call round: append assistant-with-tool-calls to the working
                // history, dispatch each call, append tool-role turns. The session
                // is NOT mutated here — only the final assistant turn lands in it.
                workingHistory.Add(new ChatTurn(
                    AgentChatRole.Assistant,
                    response.Text ?? string.Empty,
                    ToolCalls: response.ToolCalls));

                foreach (var toolCall in response.ToolCalls)
                {
                    totalToolCalls++;
                    if (_budget.MaxToolCalls is int maxToolCalls && totalToolCalls > maxToolCalls)
                    {
                        throw new AgentBudgetExceededException(nameof(RunBudget.MaxToolCalls), maxToolCalls, totalToolCalls);
                    }

                    var outcome = await _toolCallDispatcher.DispatchAsync(toolCall, context, cancellationToken).ConfigureAwait(false);
                    workingHistory.Add(new ChatTurn(
                        AgentChatRole.Tool,
                        outcome.Result ?? string.Empty,
                        ToolCallId: outcome.CallId));
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            runStopwatch.Stop();
        }

        // For usage reporting, synthesize an aggregated response carrying the
        // cross-round token totals. Last-observed ModelId wins (providers can
        // switch models mid-run in theory).
        var aggregatedResponse = lastResponse is null
            ? null
            : new CompletionResponse(
                lastResponse.Text,
                finalModelId,
                aggregatedPromptTokens > 0 ? aggregatedPromptTokens : null,
                aggregatedCompletionTokens > 0 ? aggregatedCompletionTokens : null);

        AnnotateTurnActivity(activity, aggregatedResponse, failure);

        await ReportUsageAsync(aggregatedResponse, failure, context, runStartedAt, runStopwatch.Elapsed, cancellationToken).ConfigureAwait(false);

        if (failure is not null)
        {
            var errorMessage = await ApplyErrorInterceptorsAsync(eventContext, failure, cancellationToken).ConfigureAwait(false);
            await PublishEventAsync(
                new TurnFailed(DateTimeOffset.UtcNow, eventContext, failure.GetType().Name, errorMessage, runStopwatch.Elapsed),
                cancellationToken).ConfigureAwait(false);
            throw failure;
        }

        var finalText = lastResponse!.Text;
        await _session.AppendAsync(new ChatTurn(AgentChatRole.Assistant, finalText), cancellationToken).ConfigureAwait(false);

        await PublishEventAsync(
            new TurnCompleted(
                DateTimeOffset.UtcNow,
                eventContext,
                finalText,
                finalModelId,
                aggregatedPromptTokens > 0 ? aggregatedPromptTokens : null,
                aggregatedCompletionTokens > 0 ? aggregatedCompletionTokens : null,
                runStopwatch.Elapsed),
            cancellationToken).ConfigureAwait(false);

        return finalText;
    }

    /// <summary>
    /// Stream the next assistant turn(s) as the provider produces them. Yields
    /// text deltas in order; the accumulated text of the final (non-tool-call)
    /// turn is appended to <see cref="History"/> as a single assistant turn.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Tool-using streaming (v0.4.1+).</b> When the provider surfaces tool
    /// calls on the terminal <see cref="CompletionUpdate.ToolCalls"/>, the agent
    /// dispatches each call through the <see cref="IToolCallDispatcher"/>,
    /// appends the tool-call + tool-result turns to a working history (session
    /// is untouched), and re-enters the stream loop for the next turn. The
    /// consumer sees an uninterrupted <see cref="IAsyncEnumerable{T}"/> of text
    /// deltas across all turns; tool-call observability flows through the
    /// existing <see cref="IAgentEventBus"/> (<see cref="ToolCallStarted"/> /
    /// <see cref="ToolCallCompleted"/> / <see cref="GuardrailTriggered"/>).
    /// <see cref="RunBudget"/> is enforced turn-by-turn just like in
    /// <see cref="AskAsync"/>; interrupts raised by tool guardrails flow through
    /// <see cref="AgentInterruptedException"/> as they do in AskAsync.
    /// </para>
    /// <para>
    /// <b>Filter + resilience (v0.10+).</b> The
    /// <see cref="StatefulAgentOptions.StreamingFilters"/> chain wraps the
    /// provider call on every streamed turn via
    /// <see cref="IStreamingAgentFilter.InvokeAsync"/> (around-provider); the
    /// agent fires <see cref="IStreamingAgentFilter.OnStreamDeltaAsync"/> on
    /// every filter for each yielded delta and
    /// <see cref="IStreamingAgentFilter.OnStreamCompleteAsync"/> once at end of
    /// the final (non-tool-call) turn, before output guardrails.
    /// <see cref="StatefulAgentOptions.StreamingResiliencePipeline"/> wraps the
    /// enumerator-open + first <c>MoveNextAsync</c> on each turn — transient
    /// failures that surface before the first delta are retried; once the
    /// stream is producing, yielded deltas are committed and retries stop.
    /// Filter-domain exceptions (guardrail denial, budget trip, interrupt,
    /// cancellation) are excluded from the retry predicate and surface on
    /// first firing. Input guardrails fire on every streamed turn (like
    /// <see cref="AskAsync"/>); output guardrails fire once at the end of the
    /// final turn — post-facto relative to deltas already yielded. The
    /// <see cref="StatefulAgentOptions.Filters"/> (non-streaming) chain is NOT
    /// applied on the streaming path; consumers who want request→response
    /// filter semantics use <see cref="AskAsync"/>.
    /// </para>
    /// <para>
    /// A single <see cref="TurnStarted"/> event fires at call entry and a single
    /// <see cref="TurnCompleted"/> or <see cref="TurnFailed"/> at call exit,
    /// enveloping the entire run (mirrors <see cref="AskAsync"/>). Usage
    /// telemetry is reported once after the full run drains, with token counts
    /// aggregated across every streamed turn.
    /// </para>
    /// </remarks>
    /// <param name="userMessage">User-visible text to send as the new turn.</param>
    /// <param name="cancellationToken">Cancels the stream; deltas already yielded are not retracted.</param>
    /// <exception cref="ArgumentException"><paramref name="userMessage"/> is null or whitespace.</exception>
    /// <exception cref="InvalidOperationException">The injected provider doesn't implement <see cref="IStreamingCompletionProvider"/>.</exception>
    public async IAsyncEnumerable<string> StreamAsync(
        string userMessage,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Source-compat delegation: the v0.10 `StreamAsync(string) : IAsyncEnumerable<string>`
        // surface is preserved by projecting the v0.12 full-event stream to text-only
        // via CompletionDelta.TextDelta. Input validation + ambient context stamping
        // happens here; the event-yielding core picks it up.
        if (string.IsNullOrWhiteSpace(userMessage))
        {
            throw new ArgumentException("User message must be non-empty.", nameof(userMessage));
        }
        if (_provider is not IStreamingCompletionProvider)
        {
            throw new InvalidOperationException(
                $"Provider '{_provider.ProviderName}' does not support streaming. " +
                "Inject an IStreamingCompletionProvider (both the SK and MAF adapters ship " +
                "as one) or use AskAsync for non-streaming turns.");
        }

        var context = StampRunId(_contextAccessor.Current);
        await foreach (var evt in StreamEventsCoreAsync(userMessage, context, cancellationToken).ConfigureAwait(false))
        {
            if (evt is CompletionDelta d && d.TextDelta.Length > 0)
            {
                yield return d.TextDelta;
            }
        }
    }

    /// <summary>
    /// v0.12 implementation of <see cref="IStreamingAiAgent.StreamAsync"/>. Yields
    /// the full <see cref="AgentEvent"/> taxonomy in ordering-contract order:
    /// <see cref="TurnStarted"/> → per-delta <see cref="CompletionDelta"/>s (interleaved
    /// with <see cref="ToolCallStarted"/> / <see cref="ToolCallCompleted"/> on tool-call
    /// loops) → terminal <see cref="TurnCompleted"/> or <see cref="TurnFailed"/>.
    /// Guardrail denials yield <see cref="GuardrailTriggered"/> before the final
    /// <see cref="TurnFailed"/>; interrupts yield <see cref="InterruptRaised"/>.
    /// </summary>
    public async IAsyncEnumerable<AgentEvent> StreamAsync(
        string userMessage,
        AgentContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(userMessage))
        {
            throw new ArgumentException("User message must be non-empty.", nameof(userMessage));
        }
        ArgumentNullException.ThrowIfNull(context);
        if (_provider is not IStreamingCompletionProvider)
        {
            throw new InvalidOperationException(
                $"Provider '{_provider.ProviderName}' does not support streaming. " +
                "Inject an IStreamingCompletionProvider (both the SK and MAF adapters ship " +
                "as one) or use AskAsync for non-streaming turns.");
        }

        var stamped = StampRunId(context);
        await foreach (var evt in StreamEventsCoreAsync(userMessage, stamped, cancellationToken).ConfigureAwait(false))
        {
            yield return evt;
        }
    }

    private async IAsyncEnumerable<AgentEvent> StreamEventsCoreAsync(
        string userMessage,
        AgentContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var streamingProvider = (IStreamingCompletionProvider)_provider;

        await _session.AppendAsync(new ChatTurn(AgentChatRole.User, userMessage), cancellationToken).ConfigureAwait(false);

        var eventContext = BuildEventContext(context);
        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();

        using var activity = StartTurnActivity(context);
        // Mirror AskAsync's prompt tagging so Langfuse shows the user message as the chat
        // turn's input on the streaming path too (AnnotateTurnActivity already sets the
        // completion at end-of-run).
        activity?.SetTag("gen_ai.prompt", userMessage);

        var turnStarted = new TurnStarted(startedAt, eventContext, userMessage);
        await PublishEventAsync(turnStarted, cancellationToken).ConfigureAwait(false);
        yield return turnStarted;

        // Working history starts from the session snapshot (which includes the just-
        // appended user turn) and grows with assistant-with-tool-calls + tool-result
        // turns between streamed turns. Session stays clean — only the final
        // assistant turn from the last streamed turn lands there.
        var workingHistory = new List<ChatTurn>(_session.History);

        var turnAccumulator = new StringBuilder();
        var aggregatedPromptTokens = 0;
        var aggregatedCompletionTokens = 0;
        string? finalModelId = null;
        var totalToolCalls = 0;
        var turnIndex = 0;
        var deltaSequence = 0;
        Exception? failure = null;
        var loopDone = false;

        while (!loopDone)
        {
            turnIndex++;
            if (_budget.MaxTurns is int maxTurns && turnIndex > maxTurns)
            {
                failure = new AgentBudgetExceededException(nameof(RunBudget.MaxTurns), maxTurns, turnIndex);
                break;
            }
            if (_budget.MaxDuration is TimeSpan maxDuration && sw.Elapsed > maxDuration)
            {
                failure = new AgentBudgetExceededException(nameof(RunBudget.MaxDuration), maxDuration, sw.Elapsed);
                break;
            }

            // Build the turn's request. Context providers + packer run each turn so
            // providers can react to tool results landing in the working history.
            CompletionRequest request;
            try
            {
                // Same section pipeline as AskAsyncCore — composer + providers contribute Section[],
                // resolver orders, packer applies budget, flattener produces the wire-shaped request.
                request = await BuildPerTurnRequestAsync(workingHistory, context, turnIndex, cancellationToken).ConfigureAwait(false);

                // Input guardrails fire on every model invocation — tool-call loops
                // must be able to block a mid-run escalation, not just the first turn.
                await RunInputGuardrailsAsync(request, context, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                failure = ex;
                break;
            }

            // Drain one streamed turn. Yields deltas; tracks the turn's trailing
            // tool-calls + metadata. Errors bubble through `failure` so the outer
            // loop can finalise the run.
            turnAccumulator.Clear();
            IReadOnlyList<ToolCallRequest>? turnToolCalls = null;
            string? turnModelId = null;
            int? turnPromptTokens = null;
            int? turnCompletionTokens = null;
            var skipProvider = false; // Set to true when we replay from journal
            var skipToolDispatch = false; // Set to true when we replay tool outcomes from journal
            List<ToolCallRecorded>? replayedToolOutcomes = null;

            // Full replay mode check: if we have journaled deltas for this run,
            // replay them instead of calling the provider. This enables
            // deterministic delta-by-delta reproduction on resume.
            if (_replayMode == ReplayMode.Full && context.RunId is not null && _journal is not NullAgentJournal)
            {
                var replayEntries = new List<JournalEntry>();
                await foreach (var entry in _journal.ReadAsync(context.RunId, cancellationToken))
                {
                    replayEntries.Add(entry);
                }

                var deltasToReplay = replayEntries
                    .OfType<CompletionDeltaRecorded>()
                    .Where(e => e.SequenceNumber >= deltaSequence)
                    .OrderBy(e => e.SequenceNumber)
                    .ToList();

                if (deltasToReplay.Count > 0)
                {
                    skipProvider = true;

                    // Replay journaled deltas verbatim, bypassing the provider entirely.
                    foreach (var recorded in deltasToReplay)
                    {
                        var update = recorded.Delta;

                        // Last-update-wins metadata aggregation per turn (same as live path).
                        if (update.ModelId is not null)
                        {
                            turnModelId = update.ModelId;
                            finalModelId = update.ModelId;
                        }
                        if (update.PromptTokens is not null)
                        {
                            turnPromptTokens = update.PromptTokens;
                        }
                        if (update.CompletionTokens is not null)
                        {
                            turnCompletionTokens = update.CompletionTokens;
                        }
                        if (update.ToolCalls is { Count: > 0 })
                        {
                            turnToolCalls = update.ToolCalls;
                        }

                        if (update.TextDelta.Length > 0)
                        {
                            turnAccumulator.Append(update.TextDelta);
                        }

                        // Yield the replayed delta.
                        yield return new CompletionDelta(
                            recorded.At,
                            eventContext,
                            update.TextDelta,
                            update.ModelId,
                            update.PromptTokens,
                            update.CompletionTokens,
                            update.ToolCalls);

                        // Advance delta sequence to match the replayed state.
                        deltaSequence = recorded.SequenceNumber + 1;
                    }

                    // If we replayed deltas with tool calls, replay the tool outcomes from journal
                    // and skip the tool dispatch logic to avoid re-invoking tools.
                    if (turnToolCalls is { Count: > 0 })
                    {
                        var toolOutcomes = replayEntries.OfType<ToolCallRecorded>().ToList();
                        if (toolOutcomes.Count > 0)
                        {
                            skipToolDispatch = true;
                            replayedToolOutcomes = toolOutcomes;
                            foreach (var outcome in toolOutcomes)
                            {
                                yield return new ToolCallStarted(
                                    outcome.At,
                                    eventContext,
                                    outcome.CallId,
                                    outcome.ToolName);
                                yield return new ToolCallCompleted(
                                    outcome.Outcome.Error is null ? outcome.At : outcome.At.Add(TimeSpan.FromTicks(1)),
                                    eventContext,
                                    outcome.Outcome.CallId,
                                    outcome.ToolName,
                                    outcome.Outcome.Error is null,
                                    outcome.Outcome.Error,
                                    Duration: TimeSpan.Zero);
                            }
                        }
                    }
                }
            }

            // Phase 1 — retry boundary with per-attempt telemetry. `_streamingPipeline`
            // retries the streaming-filter chain's enumerator-open + first `MoveNextAsync`
            // only. Once we observe the first delta, yielded content is committed; mid-stream
            // failures in Phase 2 surface on `failure` without replay. Filter-domain
            // exceptions are excluded from the retry predicate (see
            // `IsFilterDomainException`) so a filter-thrown denial/interrupt/budget
            // trip reaches the caller on first firing. Each retry attempt emits a
            // "stream_attempt" child span with attempt index and status tracking.
            // Skip this phase entirely if we replayed deltas from the journal.
            if (!skipProvider)
            {
                int attemptIndex = 0;
                IAsyncEnumerator<CompletionUpdate>? enumerator = null;
                CompletionUpdate? firstUpdate = null;
                var streamLive = false;

                try
                {
                await _streamingPipeline.ExecuteAsync(async attemptCt =>
                {
                    var currentAttempt = System.Threading.Interlocked.Increment(ref attemptIndex) - 1;
                    using var attemptActivity = StartAttemptActivity(currentAttempt, context, activity?.Context ?? default);

                    try
                    {
                        // Reset state from any prior attempt before re-entering the provider.
                        if (enumerator is not null)
                        {
                            await enumerator.DisposeAsync().ConfigureAwait(false);
                            enumerator = null;
                        }
                        firstUpdate = null;
                        streamLive = false;

                        var stream = InvokeThroughStreamingFilters(streamingProvider, request, attemptCt);
                        var e = stream.GetAsyncEnumerator(attemptCt);
                        try
                        {
                            if (await e.MoveNextAsync().ConfigureAwait(false))
                            {
                                enumerator = e;
                                firstUpdate = e.Current;
                                streamLive = true;
                                e = null!; // ownership transferred to outer-scope enumerator
                                attemptActivity?.SetStatus(ActivityStatusCode.Ok);
                            }
                            else
                            {
                                // Empty stream is a successful attempt (no data, but no error).
                                attemptActivity?.SetStatus(ActivityStatusCode.Ok);
                            }
                        }
                        finally
                        {
                            // Empty-stream attempt — dispose the enumerator we never promoted.
                            if (e is not null)
                            {
                                await e.DisposeAsync().ConfigureAwait(false);
                            }
                        }
                    }
                    catch (Exception ex) when (!IsFilterDomainException(ex))
                    {
                        attemptActivity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                        attemptActivity?.SetTag(AgenticTags.ErrorType, ex.GetType().Name);
                        throw;
                    }
                }, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                failure = ex;
            }

            if (failure is not null)
            {
                // Ensure any partially-promoted enumerator from the last attempt is disposed.
                if (enumerator is not null)
                {
                    await enumerator.DisposeAsync().ConfigureAwait(false);
                }
                break;
            }

            // Phase 2 — drain. `try { yield return ... } finally { dispose }` only;
            // C# forbids `yield return` inside a `try` with `catch`, so inner
            // MoveNextAsync exceptions go through local try/catch that captures
            // `failure` and breaks out of the delta loop, leaving the outer
            // try/finally to dispose the enumerator cleanly.
            // Skip Phase 2 entirely when we replayed deltas from the journal.
            if (streamLive)
            {
                var currentUpdate = firstUpdate;
                try
                {
                    while (currentUpdate is not null)
                    {
                        var update = currentUpdate;

                        // Streaming-filter delta chain per delta. A filter may transform
                        // the update or throw to abort — exceptions set `failure` and
                        // break out to the outer-loop failure path.
                        if (_streamingFilters.Count > 0)
                        {
                            var filterFailed = false;
                            try
                            {
                                foreach (var filter in _streamingFilters)
                                {
                                    update = await filter.OnStreamDeltaAsync(update, cancellationToken).ConfigureAwait(false);
                                }
                            }
                            catch (OperationCanceledException)
                            {
                                throw;
                            }
                            catch (Exception ex)
                            {
                                failure = ex;
                                filterFailed = true;
                            }
                            if (filterFailed)
                            {
                                break;
                            }
                        }

                        // Last-update-wins metadata aggregation per turn; ModelId also
                        // survives across turns so TurnCompleted carries the last seen.
                        if (update.ModelId is not null)
                        {
                            turnModelId = update.ModelId;
                            finalModelId = update.ModelId;
                        }
                        if (update.PromptTokens is not null)
                        {
                            turnPromptTokens = update.PromptTokens;
                        }
                        if (update.CompletionTokens is not null)
                        {
                            turnCompletionTokens = update.CompletionTokens;
                        }
                        if (update.ToolCalls is { Count: > 0 })
                        {
                            turnToolCalls = update.ToolCalls;
                        }

                        if (update.TextDelta.Length > 0)
                        {
                            turnAccumulator.Append(update.TextDelta);
                        }

                        // Always yield a CompletionDelta, even when TextDelta is empty — terminal
                        // updates carrying ToolCalls / final token usage / model id are important
                        // observability data for IStreamingAiAgent consumers. The string-returning
                        // overload filters to non-empty TextDelta (preserves v0.10 behaviour).
                        yield return new CompletionDelta(
                            DateTimeOffset.UtcNow,
                            eventContext,
                            update.TextDelta,
                            update.ModelId,
                            update.PromptTokens,
                            update.CompletionTokens,
                            update.ToolCalls);

                        // Journal the delta for full replay mode. This happens after yielding
                        // so the consumer receives the delta regardless of journal success.
                        if (_replayMode == ReplayMode.Full && context.RunId is not null && _journal is not NullAgentJournal)
                        {
                            try
                            {
                                await _journal.AppendAsync(new CompletionDeltaRecorded(
                                    RunId: context.RunId,
                                    SequenceNumber: deltaSequence++,
                                    Delta: update,
                                    At: DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
                            }
                            catch (Exception ex)
                            {
                                // Journal failures are logged but don't break the stream.
                                _logger.LogWarning(ex, "Journal append failed for delta {SequenceNumber}; continuing.", deltaSequence - 1);
                            }
                        }

                        // Advance — post-first-delta MoveNextAsync failures surface on `failure`
                        // and are NOT retried (yielded deltas are committed).
                        var advanced = false;
                        CompletionUpdate? nextUpdate = null;
                        try
                        {
                            if (await enumerator!.MoveNextAsync().ConfigureAwait(false))
                            {
                                nextUpdate = enumerator.Current;
                                advanced = true;
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            failure = ex;
                        }

                        if (failure is not null || !advanced)
                        {
                            break;
                        }
                        currentUpdate = nextUpdate;
                    }
                }
                finally
                {
                    if (enumerator is not null)
                    {
                        await enumerator.DisposeAsync().ConfigureAwait(false);
                    }
                }
            }
            }

            if (failure is not null)
            {
                break;
            }

            if (turnPromptTokens is int tp)
            {
                aggregatedPromptTokens += tp;
            }
            if (turnCompletionTokens is int tc)
            {
                aggregatedCompletionTokens += tc;
            }

            if (_budget.MaxPromptTokens is int maxPrompt && aggregatedPromptTokens > maxPrompt)
            {
                failure = new AgentBudgetExceededException(nameof(RunBudget.MaxPromptTokens), maxPrompt, aggregatedPromptTokens);
                break;
            }
            if (_budget.MaxCompletionTokens is int maxCompletion && aggregatedCompletionTokens > maxCompletion)
            {
                failure = new AgentBudgetExceededException(nameof(RunBudget.MaxCompletionTokens), maxCompletion, aggregatedCompletionTokens);
                break;
            }

            if (turnToolCalls is null || turnToolCalls.Count == 0)
            {
                // Final-answer turn: run OnStreamCompleteAsync + output guardrails,
                // break out of the tool-call loop. Accumulator holds the final assistant text.
                var bufferedResponse = new CompletionResponse(
                    turnAccumulator.ToString(),
                    turnModelId,
                    turnPromptTokens,
                    turnCompletionTokens);

                if (_streamingFilters.Count > 0)
                {
                    try
                    {
                        foreach (var filter in _streamingFilters)
                        {
                            await filter.OnStreamCompleteAsync(bufferedResponse, cancellationToken).ConfigureAwait(false);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        failure = ex;
                        break;
                    }
                }

                try
                {
                    await RunOutputGuardrailsAsync(bufferedResponse, context, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failure = ex;
                    break;
                }

                loopDone = true;
                break;
            }

            // Tool-call turn: append assistant-with-tool-calls to the working history,
            // dispatch each call, append tool-role turns. Session is NOT mutated.
            workingHistory.Add(new ChatTurn(
                AgentChatRole.Assistant,
                turnAccumulator.ToString(),
                ToolCalls: turnToolCalls));

            // Skip tool dispatch if we already replayed tool outcomes from journal
            if (skipToolDispatch)
            {
                // Append replayed tool outcomes to working history; replayedToolOutcomes was
                // populated in the full-replay block above so we avoid a second journal read.
                foreach (var outcome in replayedToolOutcomes ?? [])
                {
                    workingHistory.Add(new ChatTurn(
                        AgentChatRole.Tool,
                        outcome.Outcome.Result ?? string.Empty,
                        ToolCallId: outcome.CallId));
                }
                continue;
            }

            var toolFailure = false;
            foreach (var toolCall in turnToolCalls)
            {
                totalToolCalls++;
                if (_budget.MaxToolCalls is int maxToolCalls && totalToolCalls > maxToolCalls)
                {
                    failure = new AgentBudgetExceededException(nameof(RunBudget.MaxToolCalls), maxToolCalls, totalToolCalls);
                    toolFailure = true;
                    break;
                }

                // Yield ToolCallStarted BEFORE the dispatcher call — bus subscribers see the
                // same event from `DefaultToolCallDispatcher` during DispatchAsync; streaming
                // callers see it from the yield. Each observer sees it once.
                yield return new ToolCallStarted(
                    DateTimeOffset.UtcNow,
                    eventContext,
                    toolCall.CallId,
                    toolCall.ToolName);

                // Async-iterator yields return to the caller's ExecutionContext, where
                // Activity.Current is whatever the consumer has (often null when the runtime
                // is not AspNetCore-instrumented). Restore the turn activity before dispatching
                // so the gateway tool middleware chain parents under chat → grain.stream rather
                // than starting a new trace root. Same pattern as InProcessGraphOrchestrator's
                // post-yield re-anchor.
                if (activity != null) Activity.Current = activity;
                var dispatchStartedAt = DateTimeOffset.UtcNow;
                ToolCallOutcome outcome;
                try
                {
                    outcome = await _toolCallDispatcher.DispatchAsync(toolCall, context, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failure = ex;
                    toolFailure = true;
                    break;
                }
                yield return new ToolCallCompleted(
                    DateTimeOffset.UtcNow,
                    eventContext,
                    outcome.CallId,
                    toolCall.ToolName,
                    Succeeded: outcome.Error is null,
                    Error: outcome.Error,
                    Duration: DateTimeOffset.UtcNow - dispatchStartedAt);
                workingHistory.Add(new ChatTurn(
                    AgentChatRole.Tool,
                    outcome.Result ?? string.Empty,
                    ToolCallId: outcome.CallId));
            }

            if (toolFailure)
            {
                break;
            }
            // Loop back for the next streamed turn.
        }

        sw.Stop();

        var aggregatedResponse = new CompletionResponse(
            turnAccumulator.ToString(),
            finalModelId,
            aggregatedPromptTokens > 0 ? aggregatedPromptTokens : null,
            aggregatedCompletionTokens > 0 ? aggregatedCompletionTokens : null);

        AnnotateTurnActivity(activity, aggregatedResponse, failure);
        await ReportUsageAsync(failure is null ? aggregatedResponse : null, failure, context, startedAt, sw.Elapsed, cancellationToken).ConfigureAwait(false);

        if (failure is not null)
        {
            // For guardrail / interrupt failures, synthesise the semantic event before
            // TurnFailed so streaming callers see the same signal bus subscribers do.
            // HandleGuardrailOutcomeAsync already published GuardrailTriggered /
            // InterruptRaised to the bus before throwing, so bus consumers get it once;
            // these yields deliver the same event to streaming consumers once.
            if (failure is AgentGuardrailDeniedException guardrailEx)
            {
                yield return new GuardrailTriggered(
                    DateTimeOffset.UtcNow,
                    eventContext,
                    guardrailEx.Layer,
                    GuardrailDecision.Deny,
                    guardrailEx.Reason);
            }
            else if (failure is AgentInterruptedException interruptEx)
            {
                yield return new InterruptRaised(
                    DateTimeOffset.UtcNow,
                    eventContext,
                    interruptEx.Interrupt.InterruptId,
                    interruptEx.Interrupt.Reason);
            }

            var errorMessage = await ApplyErrorInterceptorsAsync(eventContext, failure, cancellationToken).ConfigureAwait(false);
            var turnFailed = new TurnFailed(
                DateTimeOffset.UtcNow,
                eventContext,
                failure.GetType().Name,
                errorMessage,
                sw.Elapsed);
            await PublishEventAsync(turnFailed, cancellationToken).ConfigureAwait(false);
            yield return turnFailed;
            throw failure;
        }

        var finalText = turnAccumulator.ToString();
        await _session.AppendAsync(new ChatTurn(AgentChatRole.Assistant, finalText), cancellationToken).ConfigureAwait(false);

        var turnCompleted = new TurnCompleted(
            DateTimeOffset.UtcNow,
            eventContext,
            finalText,
            finalModelId,
            aggregatedPromptTokens > 0 ? aggregatedPromptTokens : null,
            aggregatedCompletionTokens > 0 ? aggregatedCompletionTokens : null,
            sw.Elapsed);
        await PublishEventAsync(turnCompleted, cancellationToken).ConfigureAwait(false);
        yield return turnCompleted;
    }

    /// <summary>
    /// Continue a run that paused on an <see cref="AgentInterrupt"/>. Threads
    /// <see cref="ResumeInput.RunId"/> through as the next run's id so the
    /// tool-call dispatcher can cache-replay any journaled outcomes from the
    /// paused run — tools that already produced a result are not re-invoked.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>How the payload is routed.</b> v0.5 appends <see cref="ResumeInput.Payload"/>
    /// as the next user turn in the session — same shape as the v0.4 shim. The
    /// substantive change in v0.5 is the <see cref="ResumeInput.RunId"/> thread-through:
    /// when set, the dispatcher's cache-replay path lights up for any tool calls
    /// the LLM produces with the same <c>CallId</c>s the journal already knows
    /// about, avoiding side-effect duplication. Callers pull the <c>RunId</c>
    /// from <see cref="AgentInterruptedException.Interrupt"/>'s
    /// <see cref="AgentInterrupt.RunId"/>.
    /// </para>
    /// <para>
    /// <b>Resume without <see cref="ResumeInput.RunId"/>.</b> When <see cref="ResumeInput.RunId"/>
    /// is null, resume falls back to the v0.4 shim semantics — a fresh run with
    /// a freshly-generated <c>RunId</c>, no cache-replay. Consumers that want
    /// the shim explicitly can leave <c>RunId</c> unset.
    /// </para>
    /// <para>
    /// <b>Working-history replay.</b> v0.5 still doesn't reconstruct the
    /// interrupted run's intermediate assistant-with-tool-calls turns into the
    /// session — the resume is a new turn, not a continuation of the paused
    /// turn. Consumers that need graph-level replay will get it once the
    /// graph-orchestration pillar lands.
    /// </para>
    /// </remarks>
    /// <param name="input">Caller's decision payload plus the originating interrupt id and run id.</param>
    /// <param name="cancellationToken">Cancels the resume turn.</param>
    /// <returns>Assistant reply for the resume turn.</returns>
    public Task<string> ResumeAsync(ResumeInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var userMessage = input.Payload.ValueKind switch
        {
            System.Text.Json.JsonValueKind.String => input.Payload.GetString() ?? string.Empty,
            System.Text.Json.JsonValueKind.Undefined or System.Text.Json.JsonValueKind.Null => string.Empty,
            _ => input.Payload.ToString(),
        };
        if (string.IsNullOrWhiteSpace(userMessage))
        {
            throw new ArgumentException(
                "ResumeInput.Payload must contain a non-empty string or object; resume forwards the payload as the next user turn.",
                nameof(input));
        }
        return AskAsyncCore(userMessage, runIdOverride: input.RunId, cancellationToken);
    }

    /// <inheritdoc />
    public void Reset()
    {
        // The session contract is async, but in-process sessions complete synchronously.
        // For Orleans-backed sessions this blocks on a grain call — the same pattern
        // OrleansAiAgentProxy already uses for Reset/SystemPrompt. Callers in grain
        // contexts must route through IAgentSession.ResetAsync directly to avoid the
        // single-threaded scheduler deadlock.
        _session.ResetAsync().AsTask().GetAwaiter().GetResult();
    }

    private Activity? StartTurnActivity(AgentContext context)
    {
        // StartActivity returns null when no listener is registered — zero cost
        // for consumers that haven't wired up OpenTelemetry.
        var activity = AgenticDiagnostics.ActivitySource.StartActivity("chat", ActivityKind.Client);
        if (activity is null)
        {
            return null;
        }

        activity.SetTag(AgenticTags.GenAiSystem, _provider.ProviderName);
        activity.SetTag(AgenticTags.GenAiOperationName, "chat");
        activity.SetTag("langfuse.observation.type", "generation");

        var agentName = _agentName ?? context.AgentName;
        if (!string.IsNullOrEmpty(agentName))
        {
            activity.SetTag(AgenticTags.AgentName, agentName);
        }
        if (!string.IsNullOrEmpty(context.UserId))
        {
            activity.SetTag(AgenticTags.UserId, context.UserId);
        }
        if (!string.IsNullOrEmpty(context.TenantId))
        {
            activity.SetTag(AgenticTags.TenantId, context.TenantId);
        }
        if (!string.IsNullOrEmpty(context.CorrelationId))
        {
            activity.SetTag(AgenticTags.CorrelationId, context.CorrelationId);
        }

        return activity;
    }

    private Activity? StartAttemptActivity(int attemptIndex, AgentContext context, ActivityContext parentContext)
    {
        var activity = AgenticDiagnostics.ActivitySource.StartActivity(
            AgenticDiagnostics.StreamAttemptActivityName,
            ActivityKind.Client,
            parentContext);

        if (activity is null)
        {
            return null;
        }

        activity.SetTag(AgenticTags.GenAiSystem, _provider.ProviderName);
        activity.SetTag(AgenticTags.GenAiOperationName, "stream_attempt");
        activity.SetTag(AgenticTags.StreamAttemptIndex, attemptIndex);
        activity.SetTag(AgenticTags.StreamAttemptPhase, "retry_boundary");

        var agentName = _agentName ?? context.AgentName;
        if (!string.IsNullOrEmpty(agentName))
        {
            activity.SetTag(AgenticTags.AgentName, agentName);
        }

        return activity;
    }

    private static void AnnotateTurnActivity(Activity? activity, CompletionResponse? response, Exception? failure)
    {
        if (activity is null)
        {
            return;
        }

        if (response is not null)
        {
            activity.SetTag(AgenticTags.GenAiResponseModel, response.ModelId);
            activity.DisplayName = $"chat {response.ModelId}";

            if (response.PromptTokens is int prompt)
            {
                activity.SetTag(AgenticTags.GenAiUsageInputTokens, prompt);
            }
            if (response.CompletionTokens is int completion)
            {
                activity.SetTag(AgenticTags.GenAiUsageOutputTokens, completion);
            }

            if (!string.IsNullOrEmpty(response.Text))
            {
                activity.SetTag("gen_ai.completion", response.Text);
            }
        }

        if (failure is null)
        {
            activity.SetStatus(ActivityStatusCode.Ok);
        }
        else
        {
            activity.SetStatus(ActivityStatusCode.Error, failure.Message);
            activity.SetTag(AgenticTags.ErrorType, failure.GetType().Name);
        }
    }

    /// <summary>
    /// Build the per-turn <see cref="CompletionRequest"/> via the section pipeline:
    /// history reducer → composer (Section[] via <see cref="ISystemPromptComposer.ComposeSectionsAsync"/>)
    /// or inline <see cref="SystemPrompt"/> → base history/tools/format sections → context-provider
    /// chain (Section[] contributions) → section resolver → section window packer → flattener.
    /// </summary>
    private async Task<CompletionRequest> BuildPerTurnRequestAsync(
        IReadOnlyList<ChatTurn> workingHistory,
        AgentContext context,
        int turnIndex,
        CancellationToken cancellationToken)
    {
        var reduced = await _historyReducer.ReduceAsync(workingHistory, cancellationToken).ConfigureAwait(false);
        var tools = _toolRegistry?.Tools;
        var hasTools = tools is { Count: > 0 };

        var sections = new List<Section>(capacity: reduced.Count + 4);

        // System sections: composer emits N sections (one per contributor in the aggregating impl),
        // or the inline SystemPrompt becomes a single `system.base` section. The legacy
        // `templateSystemPrompt` mirror is preserved so context providers reading
        // `ContextInvocationContext.Candidate.SystemPrompt` see the same string the v0.4 pipeline
        // produced — useful for providers that key off the system prompt content.
        string? templateSystemPrompt = null;
        if (_systemPromptComposer is not null)
        {
            var composed = await _systemPromptComposer.ComposeSectionsAsync(context, cancellationToken).ConfigureAwait(false);
            if (composed.Count > 0)
            {
                sections.AddRange(composed);
                templateSystemPrompt = JoinSystemSegmentText(composed);
            }
        }
        else if (!string.IsNullOrEmpty(SystemPrompt))
        {
            templateSystemPrompt = SystemPrompt;
            sections.Add(new Section(
                "system.base",
                SectionKind.SystemSegment,
                new TextPayload(SystemPrompt),
                ProducerId: BaseProducerId));
        }

        AddHistoryBaseSections(sections, reduced);
        if (hasTools)
        {
            sections.Add(new Section(
                "tools.base",
                SectionKind.ToolDeclaration,
                new ToolsPayload(tools!),
                ProducerId: BaseProducerId));
        }
        else if (_responseFormat is not null)
        {
            sections.Add(new Section(
                "format.base",
                SectionKind.ResponseFormat,
                new ResponseFormatPayload(_responseFormat),
                ProducerId: BaseProducerId));
        }

        if (_contextProviders.Count > 0)
        {
            // Providers receive a CompletionRequest snapshot of the base candidate (matching
            // the v0.4 contract — they see the request as it stood before any provider
            // contributed, not an accumulated view).
            var template = new CompletionRequest(
                reduced,
                templateSystemPrompt,
                Tools: hasTools ? tools : null,
                ResponseFormat: hasTools ? null : _responseFormat);
            var invocation = new ContextInvocationContext(template, context, _session);

            foreach (var provider in _contextProviders)
            {
                // Exceptions propagate — providers are load-bearing; swallowing here would mask
                // missing retrieval results. Consumers who want swallow semantics wrap with a
                // resilience-handling provider.
                var contribution = await provider.InvokeAsync(invocation, cancellationToken).ConfigureAwait(false);
                if (contribution.Sections.Count > 0)
                {
                    sections.AddRange(contribution.Sections);
                }
            }
        }

        var resolved = await _sectionResolver.ResolveAsync(sections, cancellationToken).ConfigureAwait(false);
        var packed = await _sectionWindowPacker.PackAsync(resolved, _sectionBudget, cancellationToken).ConfigureAwait(false);

        // Telemetry fan-out runs between packer and flattener so every sink sees the same
        // per-turn snapshot. NoOp short-circuits when no sinks are wired (the common case),
        // so this is zero-cost unless observability is opted into.
        if (!_sectionTelemetryEmitter.IsNoOp)
        {
            // Overlay the agent name onto the snapshot's context when configured at the options
            // level — same pattern as BuildEventContext for AgentEvents.
            var snapshotContext = _agentName is not null && string.IsNullOrEmpty(context.AgentName)
                ? context with { AgentName = _agentName }
                : context;
            await _sectionTelemetryEmitter.EmitAsync(
                resolved,
                packed,
                _sectionBudget,
                snapshotContext,
                turnIndex,
                cancellationToken).ConfigureAwait(false);
        }

        return CompletionRequestFlattener.Flatten(packed.Sections, logger: _logger);
    }

    private static void AddHistoryBaseSections(List<Section> sections, IReadOnlyList<ChatTurn> history)
    {
        for (var i = 0; i < history.Count; i++)
        {
            var turn = history[i];
            sections.Add(new Section(
                $"history.base.{i}",
                MapTurnRoleToSectionKind(turn.Role),
                new TurnPayload(turn),
                Order: i,
                ProducerId: BaseProducerId));
        }
    }

    private static string? JoinSystemSegmentText(IReadOnlyList<Section> sections)
    {
        StringBuilder? sb = null;
        foreach (var section in sections)
        {
            if (section.Kind != SectionKind.SystemSegment || section.Payload is not TextPayload text || text.Value.Length == 0)
            {
                continue;
            }

            if (sb is null)
            {
                sb = new StringBuilder(text.Value);
            }
            else
            {
                sb.Append("\n\n").Append(text.Value);
            }
        }
        return sb?.ToString();
    }

    private const string BaseProducerId = "base";

    private static SectionKind MapTurnRoleToSectionKind(AgentChatRole role) => role switch
    {
        AgentChatRole.User => SectionKind.UserMessage,
        AgentChatRole.Assistant => SectionKind.AssistantMessage,
        AgentChatRole.Tool => SectionKind.ToolMessage,
        AgentChatRole.System => SectionKind.SystemSegment,
        _ => SectionKind.UserMessage,
    };

    private static Task RunOutputMiddlewareAsync(
        AgentOutputContext ctx,
        AgentOutputMiddleware[] middleware,
        CancellationToken cancellationToken)
    {
        Func<Task> chain = () => Task.CompletedTask;
        for (var i = middleware.Length - 1; i >= 0; i--)
        {
            var mw = middleware[i];
            var inner = chain;
            chain = () => mw.InvokeAsync(ctx, inner, cancellationToken);
        }
        return chain();
    }

    private async Task RunInputGuardrailsAsync(
        CompletionRequest request,
        AgentContext context,
        CancellationToken cancellationToken)
    {
        if (_inputGuardrails.Count == 0)
        {
            return;
        }

        foreach (var guardrail in _inputGuardrails)
        {
            var outcome = await guardrail.EvaluateAsync(request, context, cancellationToken).ConfigureAwait(false);
            await HandleGuardrailOutcomeAsync(outcome, GuardrailLayer.Input, context, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task RunOutputGuardrailsAsync(
        CompletionResponse response,
        AgentContext context,
        CancellationToken cancellationToken)
    {
        if (_outputGuardrails.Count == 0)
        {
            return;
        }

        foreach (var guardrail in _outputGuardrails)
        {
            var outcome = await guardrail.EvaluateAsync(response, context, cancellationToken).ConfigureAwait(false);
            await HandleGuardrailOutcomeAsync(outcome, GuardrailLayer.Output, context, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task HandleGuardrailOutcomeAsync(
        GuardrailOutcome outcome,
        GuardrailLayer layer,
        AgentContext context,
        CancellationToken cancellationToken)
    {
        switch (outcome.Decision)
        {
            case GuardrailDecision.Pass:
                return;
            case GuardrailDecision.Deny:
                await PublishEventAsync(
                    new GuardrailTriggered(DateTimeOffset.UtcNow, context, layer, outcome.Decision, outcome.Reason),
                    cancellationToken).ConfigureAwait(false);
                throw new AgentGuardrailDeniedException(layer, outcome.Reason);
            case GuardrailDecision.Interrupt:
                if (outcome.InterruptPayload is null)
                {
                    throw new InvalidOperationException(
                        $"Guardrail ({layer}) returned Interrupt without an AgentInterrupt payload. " +
                        "Use GuardrailOutcome.Interrupt(AgentInterrupt, reason?) to construct this outcome.");
                }
                // Stamp RunId so callers can round-trip it into ResumeInput.
                var stamped = outcome.InterruptPayload with { RunId = context.RunId };
                await PublishEventAsync(
                    new InterruptRaised(DateTimeOffset.UtcNow, context, stamped.InterruptId, stamped.Reason),
                    cancellationToken).ConfigureAwait(false);
                throw new AgentInterruptedException(stamped);
        }
    }

    private Task<CompletionResponse> InvokeThroughFiltersAsync(
        CompletionRequest request,
        CancellationToken cancellationToken)
    {
        // Build the chain lazily, right-to-left: the terminal step calls the provider.
        Func<CompletionRequest, CancellationToken, Task<CompletionResponse>> next =
            (req, ct) => _provider.CompleteAsync(req, ct);

        for (var i = _filters.Count - 1; i >= 0; i--)
        {
            var filter = _filters[i];
            var inner = next;
            next = (req, ct) => filter.InvokeAsync(req, inner, ct);
        }

        return next(request, cancellationToken);
    }

    private IAsyncEnumerable<CompletionUpdate> InvokeThroughStreamingFilters(
        IStreamingCompletionProvider streamingProvider,
        CompletionRequest request,
        CancellationToken cancellationToken)
    {
        // Same lazy right-to-left chain build as InvokeThroughFiltersAsync, adapted
        // to the streaming contract. Terminal step calls the provider's StreamAsync;
        // each filter's InvokeAsync wraps the next step in the chain. Filters that
        // don't override InvokeAsync pass through via the DIM default.
        Func<CompletionRequest, CancellationToken, IAsyncEnumerable<CompletionUpdate>> next =
            (req, ct) => streamingProvider.StreamAsync(req, ct);

        for (var i = _streamingFilters.Count - 1; i >= 0; i--)
        {
            var filter = _streamingFilters[i];
            var inner = next;
            next = (req, ct) => filter.InvokeAsync(req, inner, ct);
        }

        return next(request, cancellationToken);
    }

    private async ValueTask ReportUsageAsync(
        CompletionResponse? response,
        Exception? failure,
        AgentContext context,
        DateTimeOffset startedAt,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        try
        {
            var record = new UsageRecord(
                ProviderName: _provider.ProviderName,
                ModelId: response?.ModelId ?? "unknown",
                PromptTokens: response?.PromptTokens,
                CompletionTokens: response?.CompletionTokens,
                Duration: duration,
                StartedAt: startedAt,
                Succeeded: failure is null,
                AgentName: _agentName ?? context.AgentName,
                UserId: context.UserId,
                TenantId: context.TenantId,
                CorrelationId: context.CorrelationId,
                ErrorType: failure?.GetType().Name);

            await _usageSink.ReportAsync(record, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Usage-sink failures must not break the main flow. Log and move on.
            _logger.LogWarning(ex, "Usage sink {SinkType} threw; swallowed.", _usageSink.GetType().Name);
        }
    }

    private static string DefaultRunIdFactory() => Guid.NewGuid().ToString("N");

    /// <summary>
    /// Folds the error-interceptor chain over a failure and returns the (possibly rewritten) message.
    /// The chain may not change <see cref="Exception"/> type or suppress the failure (P9): the caller
    /// still emits <see cref="TurnFailed"/> with the original <c>ErrorType</c> and re-throws.
    /// </summary>
    private async Task<string> ApplyErrorInterceptorsAsync(AgentContext context, Exception failure, CancellationToken cancellationToken)
    {
        if (_errorInterceptors.Count == 0)
            return failure.Message;

        var errorContext = new ErrorContext(
            context.AgentName ?? string.Empty, context.RunId, NodeId: null,
            failure.GetType().Name, failure.Message);
        return await ErrorInterceptorChain.RunAsync(_errorInterceptors, errorContext, cancellationToken).ConfigureAwait(false);
    }

    private AgentContext StampRunId(AgentContext context, string? runIdOverride = null)
    {
        // Precedence: explicit override (resume) wins over ambient context.RunId,
        // which wins over the factory. This is how resume threads the interrupted
        // run's id back into the continuation so the dispatcher's cache-replay
        // path lights up on re-dispatch.
        var runId = runIdOverride ?? context.RunId ?? _runIdFactory();
        return context.RunId == runId ? context : context with { RunId = runId };
    }

    private AgentContext BuildEventContext(AgentContext context)
    {
        // Overlay the options-level agent name onto the ambient context when the
        // ambient one doesn't already carry a name. Keeps events self-descriptive
        // for consumers that wire event-bus subscribers without also setting
        // IAgentContextAccessor.Current.AgentName.
        if (_agentName is not null && string.IsNullOrEmpty(context.AgentName))
        {
            return context with { AgentName = _agentName };
        }
        return context;
    }

    private async ValueTask PublishEventAsync(AgentEvent @event, CancellationToken cancellationToken)
    {
        try
        {
            await _eventBus.PublishAsync(@event, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Bus failures must not break the main flow — same discipline as usage sink.
            _logger.LogWarning(ex, "Agent-event bus {BusType} threw on {EventType}; swallowed.",
                _eventBus.GetType().Name, @event.GetType().Name);
        }
    }

    private static ResiliencePipeline BuildDefaultPipeline() =>
        new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = 2, // 3 total attempts (1 + 2 retries)
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                Delay = TimeSpan.FromSeconds(1),
                ShouldHandle = new PredicateBuilder().Handle<Exception>(ex => !IsFilterDomainException(ex)),
            })
            .Build();

    // Per-instance variant of the default non-streaming pipeline that publishes an
    // LlmCallRetried event on each Polly retry. Built only when a real event bus is wired
    // (see ctor); otherwise the shared static _defaultPipeline is used. OnRetry fires once
    // per *failed* attempt that triggers a retry — args.AttemptNumber is the zero-based index
    // of the attempt that just failed (0 = first attempt).
    private ResiliencePipeline BuildRetryEventPipeline() =>
        new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = 2, // 3 total attempts (1 + 2 retries) — matches BuildDefaultPipeline
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                Delay = TimeSpan.FromSeconds(1),
                ShouldHandle = new PredicateBuilder().Handle<Exception>(ex => !IsFilterDomainException(ex)),
                OnRetry = args =>
                {
                    var ex = args.Outcome.Exception;
                    if (ex is not null)
                    {
                        var ctx = _contextAccessor.Current;
                        var errorType = (ex as IClassifiedAgentError)?.ErrorType ?? ex.GetType().Name;
                        var isTransient = (ex as IClassifiedAgentError)?.IsTransient ?? true;
                        // Fire-and-forget through the same swallow-on-failure helper; OnRetry is a
                        // hot-path hook, so we don't block the backoff on the bus.
                        _ = PublishEventAsync(
                            new LlmCallRetried(DateTimeOffset.UtcNow, ctx, args.AttemptNumber, errorType, isTransient),
                            CancellationToken.None);
                    }
                    return default;
                },
            })
            .Build();

    private static ResiliencePipeline BuildDefaultStreamingPipeline() =>
        new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = 2, // 3 total attempts (1 + 2 retries)
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                Delay = TimeSpan.FromSeconds(1),
                // Streaming-side predicate uses the same filter-domain exclusion as the non-
                // streaming default. The caller-level distinction between the two pipelines
                // is the *scope* of retry (pre-first-delta only on the streaming path),
                // enforced by where the pipeline is wired into StreamAsync's per-turn loop,
                // not by the predicate.
                ShouldHandle = new PredicateBuilder().Handle<Exception>(ex => !IsFilterDomainException(ex)),
            })
            .Build();

    // Centralises the rule "don't retry agent-domain exceptions" for both pipelines.
    // Filter-domain exceptions express deliberate outcomes (denial, budget trip, interrupt,
    // cancellation) that callers need to see on the first firing, not after retries mask them.
    internal static bool IsFilterDomainException(Exception ex) =>
        ex is OperationCanceledException
            or AgentGuardrailDeniedException
            or AgentBudgetExceededException
            or AgentInterruptedException;
}
