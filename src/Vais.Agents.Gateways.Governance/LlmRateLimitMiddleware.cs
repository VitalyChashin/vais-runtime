// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

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

    /// <summary>
    /// Registers <see cref="LlmRateLimitMiddleware"/> as a named factory under the key
    /// <c>"LlmRateLimit"</c>. Options are read from the manifest <c>params</c> block at
    /// agent activation time: <c>maxRequestsPerWindow</c>, <c>maxTokensPerWindow</c>,
    /// <c>windowSeconds</c> (defaults to 60).
    /// </summary>
    public static IServiceCollection AddNamedLlmGatewayMiddleware_LlmRateLimit(
        this IServiceCollection services)
    {
        services.TryAddSingleton<IRateLimitStore, InMemorySlidingWindowRateLimitStore>();
        services.AddSingleton(sp =>
        {
            var store = sp.GetRequiredService<IRateLimitStore>();
            var contextAccessor = sp.GetRequiredService<IAgentContextAccessor>();
            return new NamedLlmGatewayMiddlewareRegistration(
                "LlmRateLimit",
                (spec, _) => new LlmRateLimitMiddleware(store, ParseOptions(spec.Params), contextAccessor));
        });
        return services;
    }

    private static RateLimitOptions ParseOptions(JsonElement? paramsEl)
    {
        int? maxRequests = null, maxTokens = null;
        var window = TimeSpan.FromMinutes(1);
        if (paramsEl is { } p)
        {
            if (p.TryGetProperty("maxRequestsPerWindow", out var r) && r.TryGetInt32(out var ri))
                maxRequests = ri;
            if (p.TryGetProperty("maxTokensPerWindow", out var t) && t.TryGetInt32(out var ti))
                maxTokens = ti;
            if (p.TryGetProperty("windowSeconds", out var w) && w.TryGetInt32(out var wi))
                window = TimeSpan.FromSeconds(wi);
        }
        return new RateLimitOptions { MaxRequestsPerWindow = maxRequests, MaxTokensPerWindow = maxTokens, Window = window };
    }
}
