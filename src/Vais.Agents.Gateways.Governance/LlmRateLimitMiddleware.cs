// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.Extensions.DependencyInjection;

namespace Vais.Agents.Gateways.Governance;

/// <summary>
/// Gateway middleware that enforces sliding-window rate limits per <see cref="IAgentContextAccessor"/> key.
/// Checks limits before each LLM call and throws <see cref="AgentBudgetExceededException"/> when a limit is exceeded.
/// </summary>
public sealed class LlmRateLimitMiddleware(
    IRateLimitStore store,
    RateLimitOptions options,
    IAgentContextAccessor contextAccessor) : LlmGatewayMiddleware
{
    /// <inheritdoc/>
    protected override async Task<CompletionResponse> InvokeAsync(
        CompletionRequest request,
        Func<CompletionRequest, CancellationToken, Task<CompletionResponse>> next,
        CancellationToken cancellationToken)
    {
        await EnforceAsync(request, cancellationToken).ConfigureAwait(false);
        return await next(request, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    protected override IAsyncEnumerable<CompletionUpdate> InvokeStreamAsync(
        CompletionRequest request,
        Func<CompletionRequest, CancellationToken, IAsyncEnumerable<CompletionUpdate>> next,
        CancellationToken cancellationToken)
    {
        // Enforce synchronously before the stream starts; fire-and-forget the check via a
        // synchronous path before handing off to the streaming enumerator.
        EnforceAsync(request, cancellationToken).GetAwaiter().GetResult();
        return next(request, cancellationToken);
    }

    private async ValueTask EnforceAsync(CompletionRequest request, CancellationToken cancellationToken)
    {
        var key = GetKey();
        var estimatedTokens = options.MaxTokensPerWindow.HasValue
            ? EstimateTokens(request)
            : 0;

        var (requests, tokens) = await store.RecordAndGetAsync(
            key, estimatedTokens, options.Window, cancellationToken).ConfigureAwait(false);

        if (options.MaxRequestsPerWindow is int maxRequests && requests > maxRequests)
            throw new AgentBudgetExceededException("RateLimitRequests", maxRequests, requests);

        if (options.MaxTokensPerWindow is int maxTokens && tokens > maxTokens)
            throw new AgentBudgetExceededException("RateLimitTokens", maxTokens, tokens);
    }

    private string GetKey()
    {
        var ctx = contextAccessor.Current;
        return ctx.UserId ?? ctx.TenantId ?? ctx.WorkspaceId ?? ctx.AgentName ?? "global";
    }

    private static int EstimateTokens(CompletionRequest request)
        => request.History.Sum(t => t.Text.Length / 4) + 1;
}

/// <summary>
/// DI extension methods for registering <see cref="LlmRateLimitMiddleware"/>.
/// </summary>
public static class LlmGovernanceServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="LlmRateLimitMiddleware"/> and <see cref="InMemorySlidingWindowRateLimitStore"/>
    /// as gateway middleware using the given rate-limit options.
    /// </summary>
    public static IServiceCollection AddLlmRateLimitMiddleware(
        this IServiceCollection services,
        RateLimitOptions options)
    {
        services.AddSingleton(options);
        services.AddSingleton<IRateLimitStore, InMemorySlidingWindowRateLimitStore>();
        services.AddSingleton<LlmGatewayMiddleware, LlmRateLimitMiddleware>();
        return services;
    }
}
