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
    private readonly IContextWindowPacker _contextWindowPacker;
    private readonly ISystemPromptComposer? _systemPromptComposer;
    private readonly IReadOnlyList<IInputGuardrail> _inputGuardrails;
    private readonly IReadOnlyList<IOutputGuardrail> _outputGuardrails;
    private readonly IReadOnlyList<IStreamingAgentFilter> _streamingFilters;
    private readonly RunBudget _budget;
    private readonly IToolCallDispatcher _toolCallDispatcher;
    private readonly Func<string> _runIdFactory;
    private readonly string? _agentName;

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

        _filters = options.Filters;
        _usageSink = options.UsageSink ?? NullUsageSink.Instance;
        _eventBus = options.EventBus ?? NullAgentEventBus.Instance;
        _contextAccessor = options.ContextAccessor ?? new AsyncLocalAgentContextAccessor();
        _pipeline = options.ResiliencePipeline ?? _defaultPipeline;
        _streamingPipeline = options.StreamingResiliencePipeline ?? _defaultStreamingPipeline;
        _toolRegistry = options.ToolRegistry;
        _historyReducer = options.HistoryReducer ?? NoopHistoryReducer.Instance;
        _contextProviders = options.ContextProviders;
        _contextWindowPacker = options.ContextWindowPacker ?? NoopContextWindowPacker.Instance;
        _systemPromptComposer = options.SystemPromptComposer;
        _inputGuardrails = options.InputGuardrails;
        _outputGuardrails = options.OutputGuardrails;
        _streamingFilters = options.StreamingFilters;
        _budget = options.Budget ?? RunBudget.Unlimited;
        _toolCallDispatcher = options.ToolCallDispatcher
            ?? new DefaultToolCallDispatcher(options.ToolRegistry, options.ToolGuardrails, _eventBus, options.Journal);
        _runIdFactory = options.RunIdFactory ?? DefaultRunIdFactory;
        _agentName = options.AgentName;
        _session = options.Session ?? new InMemoryAgentSession(
            agentId: _agentName ?? "agent",
            sessionId: null,
            initialHistory: options.InitialHistory);

        SystemPrompt = options.SystemPrompt;
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

                var reduced = await _historyReducer.ReduceAsync(workingHistory, cancellationToken).ConfigureAwait(false);
                var baseSystemPrompt = _systemPromptComposer is null
                    ? SystemPrompt
                    : await _systemPromptComposer.ComposeAsync(context, cancellationToken).ConfigureAwait(false);
                var tools = _toolRegistry?.Tools;
                var candidate = new CompletionRequest(
                    reduced,
                    baseSystemPrompt,
                    Tools: tools is { Count: > 0 } ? tools : null);

                // Context-provider chain + packer run each round so providers can react
                // to tool results landing in the working history between rounds.
                candidate = await ApplyContextProvidersAsync(candidate, context, cancellationToken).ConfigureAwait(false);
                candidate = await _contextWindowPacker.PackAsync(candidate, cancellationToken).ConfigureAwait(false);

                // Input guardrails fire on every model invocation — tool-call loops
                // should be able to block a mid-run escalation, not just the first turn.
                await RunInputGuardrailsAsync(candidate, context, cancellationToken).ConfigureAwait(false);

                var response = await _pipeline.ExecuteAsync(
                    async ct => await InvokeThroughFiltersAsync(candidate, ct).ConfigureAwait(false),
                    cancellationToken).ConfigureAwait(false);
                lastResponse = response;

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
                    response.Text,
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
                        outcome.Result,
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
            await PublishEventAsync(
                new TurnFailed(DateTimeOffset.UtcNow, eventContext, failure.GetType().Name, failure.Message, runStopwatch.Elapsed),
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
                var reduced = await _historyReducer.ReduceAsync(workingHistory, cancellationToken).ConfigureAwait(false);
                var baseSystemPrompt = _systemPromptComposer is null
                    ? SystemPrompt
                    : await _systemPromptComposer.ComposeAsync(context, cancellationToken).ConfigureAwait(false);
                var tools = _toolRegistry?.Tools;
                var candidate = new CompletionRequest(
                    reduced,
                    baseSystemPrompt,
                    Tools: tools is { Count: > 0 } ? tools : null);

                candidate = await ApplyContextProvidersAsync(candidate, context, cancellationToken).ConfigureAwait(false);
                candidate = await _contextWindowPacker.PackAsync(candidate, cancellationToken).ConfigureAwait(false);

                // Input guardrails fire on every model invocation — tool-call loops
                // must be able to block a mid-run escalation, not just the first turn.
                await RunInputGuardrailsAsync(candidate, context, cancellationToken).ConfigureAwait(false);
                request = candidate;
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

            // Phase 1 — retry boundary. `_streamingPipeline` retries the streaming-
            // filter chain's enumerator-open + first `MoveNextAsync` only. Once we
            // observe the first delta, yielded content is committed; mid-stream
            // failures in Phase 2 surface on `failure` without replay. Filter-domain
            // exceptions are excluded from the retry predicate (see
            // `IsFilterDomainException`) so a filter-thrown denial/interrupt/budget
            // trip reaches the caller on first firing.
            IAsyncEnumerator<CompletionUpdate>? enumerator = null;
            CompletionUpdate? firstUpdate = null;
            var streamLive = false;

            try
            {
                await _streamingPipeline.ExecuteAsync(async attemptCt =>
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
                    outcome.Result,
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

            var turnFailed = new TurnFailed(
                DateTimeOffset.UtcNow,
                eventContext,
                failure.GetType().Name,
                failure.Message,
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

    private async Task<CompletionRequest> ApplyContextProvidersAsync(
        CompletionRequest candidate,
        AgentContext ambient,
        CancellationToken cancellationToken)
    {
        if (_contextProviders.Count == 0)
        {
            return candidate;
        }

        var invocation = new ContextInvocationContext(candidate, ambient, _session);
        var systemPrompt = candidate.SystemPrompt;
        List<ChatTurn>? historyAccum = null;
        List<ITool>? toolsAccum = null;

        foreach (var provider in _contextProviders)
        {
            // Exceptions propagate — providers are load-bearing; swallowing here
            // would mask missing retrieval results. Consumers who want swallow
            // semantics wrap with a resilience-handling provider.
            var contribution = await provider.InvokeAsync(invocation, cancellationToken).ConfigureAwait(false);

            if (!string.IsNullOrEmpty(contribution.SystemPromptAddendum))
            {
                systemPrompt = string.IsNullOrEmpty(systemPrompt)
                    ? contribution.SystemPromptAddendum
                    : systemPrompt + "\n\n" + contribution.SystemPromptAddendum;
            }
            if (contribution.InjectedHistory is { Count: > 0 } injected)
            {
                historyAccum ??= new List<ChatTurn>();
                historyAccum.AddRange(injected);
            }
            if (contribution.AdditionalTools is { Count: > 0 } addTools)
            {
                toolsAccum ??= new List<ITool>();
                toolsAccum.AddRange(addTools);
            }
        }

        if (ReferenceEquals(systemPrompt, candidate.SystemPrompt) && historyAccum is null && toolsAccum is null)
        {
            return candidate;
        }

        IReadOnlyList<ChatTurn> finalHistory = candidate.History;
        if (historyAccum is not null)
        {
            // Injected history appended AFTER session history — keeps the most
            // recent user turn at the tail where models expect it. This is the
            // canonical "here's some retrieved context, now here's the conversation"
            // layering pattern.
            var combined = new List<ChatTurn>(candidate.History.Count + historyAccum.Count);
            combined.AddRange(candidate.History);
            combined.AddRange(historyAccum);
            finalHistory = combined;
        }

        IReadOnlyList<ITool>? finalTools = candidate.Tools;
        if (toolsAccum is not null)
        {
            var combined = candidate.Tools is { Count: > 0 } existing
                ? new List<ITool>(existing.Count + toolsAccum.Count)
                : new List<ITool>(toolsAccum.Count);
            if (candidate.Tools is { Count: > 0 } existingTools)
            {
                combined.AddRange(existingTools);
            }
            combined.AddRange(toolsAccum);
            finalTools = combined;
        }

        return candidate with
        {
            History = finalHistory,
            SystemPrompt = systemPrompt,
            Tools = finalTools,
        };
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
