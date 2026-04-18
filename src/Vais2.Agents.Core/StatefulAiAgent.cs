// Copyright (c) 2026 VAIS2 Platform contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Diagnostics;
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
    private readonly List<ChatTurn> _history = new();
    private readonly IReadOnlyList<IAgentFilter> _filters;
    private readonly IUsageSink _usageSink;
    private readonly IAgentContextAccessor _contextAccessor;
    private readonly ResiliencePipeline _pipeline;
    private readonly IToolRegistry? _toolRegistry;
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
        _filters = options.Filters;
        _usageSink = options.UsageSink ?? NullUsageSink.Instance;
        _contextAccessor = options.ContextAccessor ?? new AsyncLocalAgentContextAccessor();
        _pipeline = options.ResiliencePipeline ?? _defaultPipeline;
        _toolRegistry = options.ToolRegistry;
        _agentName = options.AgentName;

        SystemPrompt = options.SystemPrompt;

        if (options.InitialHistory is { Count: > 0 } seed)
        {
            _history.AddRange(seed);
        }
    }

    /// <inheritdoc />
    public string? SystemPrompt { get; set; }

    /// <inheritdoc />
    public IReadOnlyList<ChatTurn> History => _history;

    /// <inheritdoc />
    public async Task<string> AskAsync(string userMessage, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userMessage))
        {
            throw new ArgumentException("User message must be non-empty.", nameof(userMessage));
        }

        _history.Add(new ChatTurn(ChatRole.User, userMessage));

        // Snapshot: the provider must see a stable view of the history. The
        // in-process list keeps mutating across turns; handing out the live
        // reference would race with the next call or allow an adapter to mutate
        // our state.
        var snapshot = _history.ToArray();
        var tools = _toolRegistry?.Tools;
        var request = new CompletionRequest(
            snapshot,
            SystemPrompt,
            Tools: tools is { Count: > 0 } ? tools : null);

        var context = _contextAccessor.Current;
        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();

        using var activity = StartTurnActivity(context);

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
            throw failure;
        }

        var text = response!.Text;
        _history.Add(new ChatTurn(ChatRole.Assistant, text));
        return text;
    }

    /// <inheritdoc />
    public void Reset() => _history.Clear();

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
