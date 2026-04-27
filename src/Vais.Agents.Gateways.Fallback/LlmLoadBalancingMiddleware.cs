// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.Extensions.DependencyInjection;

namespace Vais.Agents.Gateways.Fallback;

/// <summary>
/// Gateway middleware that distributes LLM calls across a pool of providers using round-robin selection.
/// Does not retry on failure — pair with <see cref="LlmFallbackMiddleware"/> for resilience.
/// </summary>
public sealed class LlmLoadBalancingMiddleware(IFallbackProviderPool pool) : LlmGatewayMiddleware
{
    private int _counter;

    /// <inheritdoc/>
    protected override Task<CompletionResponse> InvokeAsync(
        CompletionRequest request,
        Func<CompletionRequest, CancellationToken, Task<CompletionResponse>> next,
        CancellationToken cancellationToken)
    {
        var provider = Pick();
        return provider.CompleteAsync(request, cancellationToken);
    }

    /// <inheritdoc/>
    protected override IAsyncEnumerable<CompletionUpdate> InvokeStreamAsync(
        CompletionRequest request,
        Func<CompletionRequest, CancellationToken, IAsyncEnumerable<CompletionUpdate>> next,
        CancellationToken cancellationToken)
    {
        var provider = Pick();
        if (provider is not IStreamingCompletionProvider streaming)
            throw new InvalidOperationException(
                $"Load-balanced provider '{provider.ProviderName}' does not support streaming.");
        return streaming.StreamAsync(request, cancellationToken);
    }

    private ICompletionProvider Pick()
    {
        var providers = pool.GetProviders();
        if (providers.Count == 0)
            throw new InvalidOperationException("Load-balancing provider pool is empty.");
        var idx = (int)((uint)Interlocked.Increment(ref _counter) % (uint)providers.Count);
        return providers[idx];
    }
}

/// <summary>
/// DI extension methods for registering <see cref="LlmLoadBalancingMiddleware"/>.
/// </summary>
public static class LlmLoadBalancingServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="LlmLoadBalancingMiddleware"/> as gateway middleware using the given provider pool.
    /// </summary>
    public static IServiceCollection AddLlmLoadBalancingMiddleware(
        this IServiceCollection services,
        IFallbackProviderPool pool)
    {
        services.AddSingleton(pool);
        services.AddSingleton<LlmGatewayMiddleware, LlmLoadBalancingMiddleware>();
        return services;
    }
}
