// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Vais.Agents.Core;

/// <summary>
/// Gateway middleware that logs each LLM request and response at <see cref="LogLevel.Debug"/> level on both
/// the non-streaming and streaming paths. Does not mutate requests or responses.
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
        var response = await next(request, cancellationToken).ConfigureAwait(false);
        logger.LogDebug("LLM response: {PromptTokens} prompt + {CompletionTokens} completion tokens",
            response.PromptTokens, response.CompletionTokens);
        return response;
    }

    /// <inheritdoc/>
    protected override IAsyncEnumerable<CompletionUpdate> InvokeStreamAsync(
        CompletionRequest request,
        Func<CompletionRequest, CancellationToken, IAsyncEnumerable<CompletionUpdate>> next,
        CancellationToken cancellationToken)
    {
        logger.LogDebug("LLM stream start: {TurnCount} turns, {ToolCount} tools",
            request.History.Count, request.Tools?.Count ?? 0);
        return next(request, cancellationToken);
    }

    /// <inheritdoc/>
    protected override ValueTask OnStreamCompleteAsync(
        CompletionResponse final,
        CancellationToken cancellationToken = default)
    {
        logger.LogDebug("LLM stream complete: {PromptTokens} prompt + {CompletionTokens} completion tokens",
            final.PromptTokens, final.CompletionTokens);
        return ValueTask.CompletedTask;
    }
}

public static partial class LlmGatewayServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="LlmLoggingMiddleware"/> as gateway middleware. Logs each LLM request and
    /// response at <see cref="LogLevel.Debug"/> on both paths.
    /// </summary>
    public static IServiceCollection AddLlmLoggingMiddleware(
        this IServiceCollection services)
        => services.AddLlmGatewayMiddleware<LlmLoggingMiddleware>();
}
