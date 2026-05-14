// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Vais.Agents.Core;

/// <summary>
/// Gateway middleware that logs each LLM call at <see cref="LogLevel.Information"/> level on both
/// the non-streaming and streaming paths. Emits one structured line per call with model, token
/// counts, and latency. Does not mutate requests or responses.
/// </summary>
public sealed class LlmLoggingMiddleware(ILogger<LlmLoggingMiddleware> logger) : LlmGatewayMiddleware
{
    /// <inheritdoc/>
    protected override async Task<CompletionResponse> InvokeAsync(
        CompletionRequest request,
        Func<CompletionRequest, CancellationToken, Task<CompletionResponse>> next,
        CancellationToken cancellationToken)
    {
        logger.LogDebug("LLM request: {TurnCount} turns, {ToolCount} tools",
            request.History.Count, request.Tools?.Count ?? 0);
        var sw = Stopwatch.StartNew();
        var response = await next(request, cancellationToken).ConfigureAwait(false);
        sw.Stop();
        logger.LogInformation(
            "LLM call: model={Model} promptTokens={PromptTokens} completionTokens={CompletionTokens} latencyMs={LatencyMs}",
            response.ModelId, response.PromptTokens, response.CompletionTokens, sw.ElapsedMilliseconds);
        return response;
    }

    /// <inheritdoc/>
    protected override IAsyncEnumerable<CompletionUpdate> InvokeStreamAsync(
        CompletionRequest request,
        Func<CompletionRequest, CancellationToken, IAsyncEnumerable<CompletionUpdate>> next,
        CancellationToken cancellationToken)
        => StreamWithLoggingAsync(request, next, cancellationToken);

    private async IAsyncEnumerable<CompletionUpdate> StreamWithLoggingAsync(
        CompletionRequest request,
        Func<CompletionRequest, CancellationToken, IAsyncEnumerable<CompletionUpdate>> next,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        logger.LogDebug("LLM stream start: {TurnCount} turns, {ToolCount} tools",
            request.History.Count, request.Tools?.Count ?? 0);
        await foreach (var update in next(request, cancellationToken).ConfigureAwait(false))
            yield return update;
    }

    /// <inheritdoc/>
    protected override ValueTask OnStreamCompleteAsync(
        CompletionResponse final,
        CancellationToken cancellationToken = default)
    {
        logger.LogInformation(
            "LLM call (stream): model={Model} promptTokens={PromptTokens} completionTokens={CompletionTokens}",
            final.ModelId, final.PromptTokens, final.CompletionTokens);
        return ValueTask.CompletedTask;
    }
}

public static partial class LlmGatewayServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="LlmLoggingMiddleware"/> as gateway middleware. Logs each LLM call at
    /// <see cref="LogLevel.Information"/> with model, token counts, and latency on both paths.
    /// </summary>
    public static IServiceCollection AddLlmLoggingMiddleware(
        this IServiceCollection services)
        => services.AddLlmGatewayMiddleware<LlmLoggingMiddleware>();
}
