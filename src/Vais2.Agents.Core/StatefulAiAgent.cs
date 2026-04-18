// Copyright (c) 2026 VAIS2 Platform contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Polly;
using Polly.Retry;

namespace Vais2.Agents.Core;

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
public sealed class StatefulAiAgent : IAiAgent
{
    private static readonly ResiliencePipeline _defaultPipeline = BuildDefaultPipeline();

    private readonly ICompletionProvider _provider;
    private readonly ILogger<StatefulAiAgent> _logger;
    private readonly IAgentSession _session;
    private readonly IReadOnlyList<IAgentFilter> _filters;
    private readonly IUsageSink _usageSink;
    private readonly IAgentEventBus _eventBus;
    private readonly IAgentContextAccessor _contextAccessor;
    private readonly ResiliencePipeline _pipeline;
    private readonly IToolRegistry? _toolRegistry;
    private readonly IHistoryReducer _historyReducer;
    private readonly IReadOnlyList<IContextProvider> _contextProviders;
    private readonly IContextWindowPacker _contextWindowPacker;
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
        _toolRegistry = options.ToolRegistry;
        _historyReducer = options.HistoryReducer ?? NoopHistoryReducer.Instance;
        _contextProviders = options.ContextProviders;
        _contextWindowPacker = options.ContextWindowPacker ?? NoopContextWindowPacker.Instance;
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
    public async Task<string> AskAsync(string userMessage, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userMessage))
        {
            throw new ArgumentException("User message must be non-empty.", nameof(userMessage));
        }

        await _session.AppendAsync(new ChatTurn(AgentChatRole.User, userMessage), cancellationToken).ConfigureAwait(false);

        // Snapshot: the provider must see a stable view of the history. The
        // session may keep mutating across turns; handing out the live reference
        // would race with the next call or allow an adapter to mutate our state.
        var snapshot = _session.History.ToArray();
        var reduced = await _historyReducer.ReduceAsync(snapshot, cancellationToken).ConfigureAwait(false);
        var tools = _toolRegistry?.Tools;
        var candidate = new CompletionRequest(
            reduced,
            SystemPrompt,
            Tools: tools is { Count: > 0 } ? tools : null);

        var context = _contextAccessor.Current;

        // Context-provider chain: each provider reads the candidate + returns
        // a contribution the host merges into the request. Packer runs after
        // all providers so it sees the final pre-filter shape.
        candidate = await ApplyContextProvidersAsync(candidate, context, cancellationToken).ConfigureAwait(false);
        candidate = await _contextWindowPacker.PackAsync(candidate, cancellationToken).ConfigureAwait(false);
        var request = candidate;

        var eventContext = BuildEventContext(context);
        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();

        using var activity = StartTurnActivity(context);

        await PublishEventAsync(new TurnStarted(startedAt, eventContext, userMessage), cancellationToken).ConfigureAwait(false);

        CompletionResponse? response = null;
        Exception? failure = null;

        try
        {
            response = await _pipeline.ExecuteAsync(
                async ct => await InvokeThroughFiltersAsync(request, ct).ConfigureAwait(false),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancellation is not a "failure" for usage-sink purposes — it's the
            // caller asking to stop. Re-throw without reporting; UsageRecord is
            // reserved for completed or errored turns.
            throw;
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            sw.Stop();
        }

        AnnotateTurnActivity(activity, response, failure);

        await ReportUsageAsync(response, failure, context, startedAt, sw.Elapsed, cancellationToken).ConfigureAwait(false);

        if (failure is not null)
        {
            await PublishEventAsync(
                new TurnFailed(DateTimeOffset.UtcNow, eventContext, failure.GetType().Name, failure.Message, sw.Elapsed),
                cancellationToken).ConfigureAwait(false);
            throw failure;
        }

        var text = response!.Text;
        await _session.AppendAsync(new ChatTurn(AgentChatRole.Assistant, text), cancellationToken).ConfigureAwait(false);

        await PublishEventAsync(
            new TurnCompleted(DateTimeOffset.UtcNow, eventContext, text, response.ModelId, response.PromptTokens, response.CompletionTokens, sw.Elapsed),
            cancellationToken).ConfigureAwait(false);

        return text;
    }

    /// <summary>
    /// Stream the next assistant turn as it's produced by the provider. Yields
    /// text deltas in order; after the stream drains, the accumulated text is
    /// appended to <see cref="History"/> as a single assistant turn (so a caller
    /// that alternates <see cref="AskAsync"/> and <c>StreamAsync</c> sees a
    /// well-formed history in both cases).
    /// </summary>
    /// <remarks>
    /// <para>
    /// V1 scope: <see cref="StatefulAgentOptions.Filters"/> and
    /// <see cref="StatefulAgentOptions.ResiliencePipeline"/> are NOT applied to
    /// streaming turns. Filter and resilience surfaces are both synchronous
    /// request→response; wrapping a stream in them either buffers the whole
    /// response (defeating the point) or requires a streaming-filter API we
    /// haven't designed yet. Consumers who need filter-mediated behaviour
    /// (knowledge retrieval, prompt enrichment) on streaming turns should
    /// either stay on <see cref="AskAsync"/> or run retrieval manually and
    /// mutate <see cref="SystemPrompt"/> before calling <c>StreamAsync</c>.
    /// </para>
    /// <para>
    /// Usage telemetry and the per-turn <see cref="Activity"/> ARE emitted —
    /// usage is reported once after the stream drains, with aggregated token
    /// counts from the final update when the provider supplies them.
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
        if (string.IsNullOrWhiteSpace(userMessage))
        {
            throw new ArgumentException("User message must be non-empty.", nameof(userMessage));
        }

        if (_provider is not IStreamingCompletionProvider streamingProvider)
        {
            throw new InvalidOperationException(
                $"Provider '{_provider.ProviderName}' does not support streaming. " +
                "Inject an IStreamingCompletionProvider (both the SK and MAF adapters ship " +
                "as one) or use AskAsync for non-streaming turns.");
        }

        await _session.AppendAsync(new ChatTurn(AgentChatRole.User, userMessage), cancellationToken).ConfigureAwait(false);

        var snapshot = _session.History.ToArray();
        var reduced = await _historyReducer.ReduceAsync(snapshot, cancellationToken).ConfigureAwait(false);
        var tools = _toolRegistry?.Tools;
        var candidate = new CompletionRequest(
            reduced,
            SystemPrompt,
            Tools: tools is { Count: > 0 } ? tools : null);

        var context = _contextAccessor.Current;

        candidate = await ApplyContextProvidersAsync(candidate, context, cancellationToken).ConfigureAwait(false);
        candidate = await _contextWindowPacker.PackAsync(candidate, cancellationToken).ConfigureAwait(false);
        var request = candidate;

        var eventContext = BuildEventContext(context);
        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();

        using var activity = StartTurnActivity(context);

        await PublishEventAsync(new TurnStarted(startedAt, eventContext, userMessage), cancellationToken).ConfigureAwait(false);

        var accumulator = new StringBuilder();
        string? finalModelId = null;
        int? finalPromptTokens = null;
        int? finalCompletionTokens = null;
        Exception? failure = null;

        IAsyncEnumerator<CompletionUpdate>? enumerator = null;
        try
        {
            enumerator = streamingProvider.StreamAsync(request, cancellationToken).GetAsyncEnumerator(cancellationToken);

            while (true)
            {
                bool hasNext;
                CompletionUpdate? update = null;
                try
                {
                    hasNext = await enumerator.MoveNextAsync().ConfigureAwait(false);
                    if (hasNext)
                    {
                        update = enumerator.Current;
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

                if (!hasNext)
                {
                    break;
                }

                // Provider-side metadata on any update overwrites — final update's values win
                // because it's emitted last.
                if (update!.ModelId is not null)
                {
                    finalModelId = update.ModelId;
                }
                if (update.PromptTokens is not null)
                {
                    finalPromptTokens = update.PromptTokens;
                }
                if (update.CompletionTokens is not null)
                {
                    finalCompletionTokens = update.CompletionTokens;
                }

                if (update.TextDelta.Length > 0)
                {
                    accumulator.Append(update.TextDelta);
                    yield return update.TextDelta;
                }
            }
        }
        finally
        {
            sw.Stop();
            if (enumerator is not null)
            {
                await enumerator.DisposeAsync().ConfigureAwait(false);
            }
        }

        var bufferedResponse = failure is null
            ? new CompletionResponse(accumulator.ToString(), finalModelId, finalPromptTokens, finalCompletionTokens)
            : null;
        AnnotateTurnActivity(activity, bufferedResponse, failure);
        await ReportUsageAsync(bufferedResponse, failure, context, startedAt, sw.Elapsed, cancellationToken).ConfigureAwait(false);

        if (failure is not null)
        {
            await PublishEventAsync(
                new TurnFailed(DateTimeOffset.UtcNow, eventContext, failure.GetType().Name, failure.Message, sw.Elapsed),
                cancellationToken).ConfigureAwait(false);
            throw failure;
        }

        var finalText = accumulator.ToString();
        await _session.AppendAsync(new ChatTurn(AgentChatRole.Assistant, finalText), cancellationToken).ConfigureAwait(false);

        await PublishEventAsync(
            new TurnCompleted(DateTimeOffset.UtcNow, eventContext, finalText, finalModelId, finalPromptTokens, finalCompletionTokens, sw.Elapsed),
            cancellationToken).ConfigureAwait(false);
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
                ShouldHandle = new PredicateBuilder().Handle<Exception>(ex => ex is not OperationCanceledException),
            })
            .Build();
}
