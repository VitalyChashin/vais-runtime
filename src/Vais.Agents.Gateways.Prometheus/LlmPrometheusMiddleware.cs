// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Prometheus;

namespace Vais.Agents.Gateways.Prometheus;

/// <summary>
/// <see cref="LlmGatewayMiddleware"/> that emits per-call Prometheus metrics:
/// <list type="bullet">
///   <item><c>llm_requests_total{model,workspace,status}</c> — request count; status is "success" or "error".</item>
///   <item><c>llm_request_duration_seconds{model,workspace}</c> — latency histogram.</item>
///   <item><c>llm_tokens_total{model,workspace,type}</c> — token consumption; type is "prompt" or "completion".</item>
/// </list>
/// </summary>
/// <remarks>
/// Metrics are created in the <see cref="MetricFactory"/> supplied at construction.
/// The single-parameter constructor (used by DI) writes to the default Prometheus
/// registry. Pass an isolated <c>MetricFactory</c> from
/// <c>Metrics.WithCustomRegistry(Metrics.NewCustomRegistry())</c> in tests to avoid
/// global state pollution.
/// </remarks>
public sealed class LlmPrometheusMiddleware : LlmGatewayMiddleware
{
    private static readonly AsyncLocal<StreamCallState?> _streamStateSlot = new();

    private readonly IAgentContextAccessor _contextAccessor;
    private readonly Counter _requestsTotal;
    private readonly Histogram _requestDuration;
    private readonly Counter _tokensTotal;

    /// <summary>
    /// Initializes the middleware using the default Prometheus registry.
    /// Intended for DI registration via <see cref="LlmPrometheusServiceCollectionExtensions.AddLlmPrometheusMiddleware"/>.
    /// </summary>
    public LlmPrometheusMiddleware(IAgentContextAccessor contextAccessor)
        : this(contextAccessor, Metrics.WithCustomRegistry(Metrics.DefaultRegistry))
    {
    }

    /// <summary>
    /// Initializes the middleware writing metrics to the supplied <paramref name="metricFactory"/>.
    /// Use this constructor in tests to provide an isolated registry.
    /// </summary>
    public LlmPrometheusMiddleware(IAgentContextAccessor contextAccessor, MetricFactory metricFactory)
    {
        ArgumentNullException.ThrowIfNull(contextAccessor);
        ArgumentNullException.ThrowIfNull(metricFactory);

        _contextAccessor = contextAccessor;

        _requestsTotal = metricFactory.CreateCounter(
            "llm_requests_total",
            "Total LLM completion requests.",
            new CounterConfiguration { LabelNames = ["model", "workspace", "status"] });

        _requestDuration = metricFactory.CreateHistogram(
            "llm_request_duration_seconds",
            "LLM completion request duration.",
            new HistogramConfiguration { LabelNames = ["model", "workspace"] });

        _tokensTotal = metricFactory.CreateCounter(
            "llm_tokens_total",
            "Total LLM tokens consumed.",
            new CounterConfiguration { LabelNames = ["model", "workspace", "type"] });
    }

    /// <inheritdoc/>
    protected override async Task<CompletionResponse> InvokeAsync(
        CompletionRequest request,
        Func<CompletionRequest, CancellationToken, Task<CompletionResponse>> next,
        CancellationToken cancellationToken)
    {
        var workspace = _contextAccessor.Current.WorkspaceId ?? "_default";
        var sw = Stopwatch.StartNew();
        try
        {
            var response = await next(request, cancellationToken).ConfigureAwait(false);
            sw.Stop();
            var model = response.ModelId ?? "";
            _requestDuration.WithLabels(model, workspace).Observe(sw.Elapsed.TotalSeconds);
            _requestsTotal.WithLabels(model, workspace, "success").Inc();
            if (response.PromptTokens.HasValue)
                _tokensTotal.WithLabels(model, workspace, "prompt").Inc(response.PromptTokens.Value);
            if (response.CompletionTokens.HasValue)
                _tokensTotal.WithLabels(model, workspace, "completion").Inc(response.CompletionTokens.Value);
            return response;
        }
        catch
        {
            sw.Stop();
            _requestDuration.WithLabels("", workspace).Observe(sw.Elapsed.TotalSeconds);
            _requestsTotal.WithLabels("", workspace, "error").Inc();
            throw;
        }
    }

    /// <inheritdoc/>
    protected override IAsyncEnumerable<CompletionUpdate> InvokeStreamAsync(
        CompletionRequest request,
        Func<CompletionRequest, CancellationToken, IAsyncEnumerable<CompletionUpdate>> next,
        CancellationToken cancellationToken)
    {
        _streamStateSlot.Value = new StreamCallState(
            Workspace: _contextAccessor.Current.WorkspaceId ?? "_default",
            Stopwatch: Stopwatch.StartNew());

        return next(request, cancellationToken);
    }

    /// <inheritdoc/>
    protected override ValueTask OnStreamCompleteAsync(
        CompletionResponse final,
        CancellationToken cancellationToken = default)
    {
        var state = _streamStateSlot.Value;
        if (state is null)
            return ValueTask.CompletedTask;

        state.Stopwatch.Stop();
        var model = final.ModelId ?? "";
        _requestDuration.WithLabels(model, state.Workspace).Observe(state.Stopwatch.Elapsed.TotalSeconds);
        _requestsTotal.WithLabels(model, state.Workspace, "success").Inc();
        if (final.PromptTokens.HasValue)
            _tokensTotal.WithLabels(model, state.Workspace, "prompt").Inc(final.PromptTokens.Value);
        if (final.CompletionTokens.HasValue)
            _tokensTotal.WithLabels(model, state.Workspace, "completion").Inc(final.CompletionTokens.Value);

        return ValueTask.CompletedTask;
    }

    private sealed record StreamCallState(string Workspace, Stopwatch Stopwatch);
}

/// <summary>Extension methods for registering <see cref="LlmPrometheusMiddleware"/>.</summary>
public static class LlmPrometheusServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="LlmPrometheusMiddleware"/> in the <see cref="LlmGatewayMiddleware"/>
    /// pipeline. Metrics are written to the default Prometheus registry.
    /// </summary>
    public static IServiceCollection AddLlmPrometheusMiddleware(this IServiceCollection services)
    {
        services.AddSingleton<LlmGatewayMiddleware, LlmPrometheusMiddleware>();
        return services;
    }

    /// <summary>
    /// Registers <see cref="LlmPrometheusMiddleware"/> as a named factory under the key
    /// <c>"Prometheus"</c> so it can be referenced from <c>LlmGatewayConfig</c> middleware lists.
    /// </summary>
    public static IServiceCollection AddNamedLlmGatewayMiddleware_Prometheus(
        this IServiceCollection services)
        => services.AddSingleton(
            sp => new NamedLlmGatewayMiddlewareRegistration(
                "Prometheus",
                (_, _) => new LlmPrometheusMiddleware(
                    sp.GetRequiredService<IAgentContextAccessor>())));
}
