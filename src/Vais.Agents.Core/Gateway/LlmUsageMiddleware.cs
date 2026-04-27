// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.DependencyInjection;

namespace Vais.Agents.Core;

/// <summary>
/// Gateway middleware that reports token usage to <see cref="IUsageSink"/> after every LLM call —
/// once per non-streaming turn and once per stream completion. Captures wall-clock timing
/// around the provider call so <see cref="UsageRecord.Duration"/> is accurate.
/// </summary>
/// <remarks>
/// <see cref="UsageRecord.ProviderName"/> is set to <c>"gateway"</c> because gateway middleware
/// operates above the individual provider and does not have access to its name.
/// </remarks>
public sealed class LlmUsageMiddleware(IUsageSink sink, IAgentContextAccessor contextAccessor)
    : LlmGatewayMiddleware
{
    /// <inheritdoc/>
    protected override async Task<CompletionResponse> InvokeAsync(
        CompletionRequest request,
        Func<CompletionRequest, CancellationToken, Task<CompletionResponse>> next,
        CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();
        var response = await next(request, cancellationToken).ConfigureAwait(false);
        await ReportAsync(response, startedAt, sw.Elapsed, cancellationToken).ConfigureAwait(false);
        return response;
    }

    /// <inheritdoc/>
    protected override IAsyncEnumerable<CompletionUpdate> InvokeStreamAsync(
        CompletionRequest request,
        Func<CompletionRequest, CancellationToken, IAsyncEnumerable<CompletionUpdate>> next,
        CancellationToken cancellationToken)
        => StreamAndReportAsync(next(request, cancellationToken), cancellationToken);

    private async IAsyncEnumerable<CompletionUpdate> StreamAndReportAsync(
        IAsyncEnumerable<CompletionUpdate> source,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();
        var sb = new StringBuilder();
        string? modelId = null;
        int? promptTokens = null;
        int? completionTokens = null;
        IReadOnlyList<ToolCallRequest>? toolCalls = null;
        await foreach (var update in source.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            sb.Append(update.TextDelta);
            modelId ??= update.ModelId;
            promptTokens = update.PromptTokens ?? promptTokens;
            completionTokens = update.CompletionTokens ?? completionTokens;
            toolCalls = update.ToolCalls ?? toolCalls;
            yield return update;
        }
        sw.Stop();
        var final = new CompletionResponse(sb.ToString(), modelId, promptTokens, completionTokens, toolCalls);
        await ReportAsync(final, startedAt, sw.Elapsed, cancellationToken).ConfigureAwait(false);
    }

    private ValueTask ReportAsync(
        CompletionResponse response,
        DateTimeOffset startedAt,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        var ctx = contextAccessor.Current;
        var record = new UsageRecord(
            ProviderName: "gateway",
            ModelId: response.ModelId ?? "unknown",
            PromptTokens: response.PromptTokens,
            CompletionTokens: response.CompletionTokens,
            Duration: duration,
            StartedAt: startedAt,
            Succeeded: true,
            AgentName: ctx.AgentName,
            UserId: ctx.UserId,
            TenantId: ctx.TenantId,
            CorrelationId: ctx.CorrelationId,
            WorkspaceId: ctx.WorkspaceId);
        return sink.ReportAsync(record, cancellationToken);
    }
}

public static partial class LlmGatewayServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="LlmUsageMiddleware"/> as gateway middleware. Reports token usage to the
    /// registered <see cref="IUsageSink"/> after every non-streaming turn and every streaming completion.
    /// </summary>
    public static IServiceCollection AddLlmUsageMiddleware(
        this IServiceCollection services)
        => services.AddLlmGatewayMiddleware<LlmUsageMiddleware>();
}
