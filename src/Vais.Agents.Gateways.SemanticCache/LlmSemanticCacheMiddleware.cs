// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.DependencyInjection;

namespace Vais.Agents.Gateways.SemanticCache;

/// <summary>
/// Gateway middleware that short-circuits LLM calls by returning a cached
/// <see cref="CompletionResponse"/> for repeated prompts. Uses the last user turn's
/// text as the cache key. Operates on both the non-streaming and streaming paths.
/// </summary>
public sealed class LlmSemanticCacheMiddleware(ISemanticCacheStore store) : LlmGatewayMiddleware
{
    /// <inheritdoc/>
    protected override async Task<CompletionResponse> InvokeAsync(
        CompletionRequest request,
        Func<CompletionRequest, CancellationToken, Task<CompletionResponse>> next,
        CancellationToken cancellationToken)
    {
        var key = GetCacheKey(request);
        var cached = await store.GetAsync(key, cancellationToken).ConfigureAwait(false);
        if (cached is not null) return cached;

        var response = await next(request, cancellationToken).ConfigureAwait(false);
        await store.SetAsync(key, response, cancellationToken).ConfigureAwait(false);
        return response;
    }

    /// <inheritdoc/>
    protected override IAsyncEnumerable<CompletionUpdate> InvokeStreamAsync(
        CompletionRequest request,
        Func<CompletionRequest, CancellationToken, IAsyncEnumerable<CompletionUpdate>> next,
        CancellationToken cancellationToken)
        => StreamWithCacheAsync(request, next, cancellationToken);

    private async IAsyncEnumerable<CompletionUpdate> StreamWithCacheAsync(
        CompletionRequest request,
        Func<CompletionRequest, CancellationToken, IAsyncEnumerable<CompletionUpdate>> next,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var key = GetCacheKey(request);
        var cached = await store.GetAsync(key, cancellationToken).ConfigureAwait(false);
        if (cached is not null)
        {
            yield return new CompletionUpdate(cached.Text, cached.ModelId,
                cached.PromptTokens, cached.CompletionTokens, cached.ToolCalls);
            yield break;
        }

        var sb = new StringBuilder();
        string? modelId = null;
        int? promptTokens = null;
        int? completionTokens = null;
        IReadOnlyList<ToolCallRequest>? toolCalls = null;

        await foreach (var update in next(request, cancellationToken).ConfigureAwait(false))
        {
            sb.Append(update.TextDelta);
            modelId ??= update.ModelId;
            promptTokens = update.PromptTokens ?? promptTokens;
            completionTokens = update.CompletionTokens ?? completionTokens;
            toolCalls = update.ToolCalls ?? toolCalls;
            yield return update;
        }

        var final = new CompletionResponse(sb.ToString(), modelId, promptTokens, completionTokens, toolCalls);
        await store.SetAsync(key, final, cancellationToken).ConfigureAwait(false);
    }

    private static string GetCacheKey(CompletionRequest request)
    {
        for (var i = request.History.Count - 1; i >= 0; i--)
        {
            if (request.History[i].Role == AgentChatRole.User)
                return request.History[i].Text;
        }
        return string.Empty;
    }
}

/// <summary>
/// DI extension methods for registering <see cref="LlmSemanticCacheMiddleware"/>.
/// </summary>
public static class LlmSemanticCacheServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="LlmSemanticCacheMiddleware"/> and <see cref="InMemorySemanticCacheStore"/>
    /// as gateway middleware. Short-circuits repeated LLM calls using an in-memory exact-match cache.
    /// </summary>
    public static IServiceCollection AddLlmSemanticCacheMiddleware(
        this IServiceCollection services)
    {
        services.AddSingleton<ISemanticCacheStore, InMemorySemanticCacheStore>();
        services.AddSingleton<LlmGatewayMiddleware, LlmSemanticCacheMiddleware>();
        return services;
    }

    /// <summary>
    /// Registers <see cref="LlmSemanticCacheMiddleware"/> as a named factory under the key
    /// <c>"SemanticCache"</c>. The middleware resolves <see cref="ISemanticCacheStore"/> from DI
    /// at agent activation time; the store must be registered separately.
    /// </summary>
    public static IServiceCollection AddNamedLlmGatewayMiddleware_SemanticCache(
        this IServiceCollection services)
        => services.AddSingleton(
            sp => new NamedLlmGatewayMiddlewareRegistration(
                "SemanticCache",
                (_, _) => new LlmSemanticCacheMiddleware(
                    sp.GetRequiredService<ISemanticCacheStore>())));
}
