// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;

namespace Vais.Agents.Gateways.Fallback;

/// <summary>
/// Gateway middleware that tries each provider in the pool in order, falling back to the next
/// on any exception. Operates on both the non-streaming and streaming paths.
/// </summary>
/// <remarks>
/// On the streaming path, fallback is committed on the first successful delta — once the stream
/// has started delivering updates, it is not retried. This matches the idempotence contract of
/// <see cref="IStreamingCompletionProvider"/>.
/// </remarks>
public sealed class LlmFallbackMiddleware(IFallbackProviderPool pool) : LlmGatewayMiddleware
{
    /// <inheritdoc/>
    protected override async Task<CompletionResponse> InvokeAsync(
        CompletionRequest request,
        Func<CompletionRequest, CancellationToken, Task<CompletionResponse>> next,
        CancellationToken cancellationToken)
    {
        var providers = pool.GetProviders();
        Exception? last = null;

        foreach (var provider in providers)
        {
            try
            {
                return await provider.CompleteAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                last = ex;
            }
        }

        throw last ?? new InvalidOperationException("Fallback provider pool is empty.");
    }

    /// <inheritdoc/>
    protected override IAsyncEnumerable<CompletionUpdate> InvokeStreamAsync(
        CompletionRequest request,
        Func<CompletionRequest, CancellationToken, IAsyncEnumerable<CompletionUpdate>> next,
        CancellationToken cancellationToken)
        => StreamWithFallbackAsync(request, cancellationToken);

    private async IAsyncEnumerable<CompletionUpdate> StreamWithFallbackAsync(
        CompletionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var providers = pool.GetProviders();
        IAsyncEnumerator<CompletionUpdate>? committed = null;
        CompletionUpdate firstDelta = default!;
        Exception? last = null;

        // Phase 1: find the first provider that successfully delivers at least one delta.
        // yield return is NOT used here — C# disallows yield inside try/catch.
        foreach (var provider in providers)
        {
            if (provider is not IStreamingCompletionProvider streaming) continue;

            var enumerator = streaming.StreamAsync(request, cancellationToken).GetAsyncEnumerator(cancellationToken);
            bool hasDelta;
            try
            {
                hasDelta = await enumerator.MoveNextAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                last = ex;
                await enumerator.DisposeAsync().ConfigureAwait(false);
                continue;
            }

            if (hasDelta)
            {
                committed = enumerator;
                firstDelta = enumerator.Current;
                break;
            }

            await enumerator.DisposeAsync().ConfigureAwait(false);
        }

        if (committed is null)
            throw last ?? new InvalidOperationException("No streaming provider in the fallback pool succeeded.");

        // Phase 2: yield the committed provider's stream. yield return is allowed in try/finally.
        await using (committed.ConfigureAwait(false))
        {
            yield return firstDelta;
            while (await committed.MoveNextAsync().ConfigureAwait(false))
                yield return committed.Current;
        }
    }
}

/// <summary>
/// DI extension methods for registering <see cref="LlmFallbackMiddleware"/>.
/// </summary>
public static class LlmFallbackServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="LlmFallbackMiddleware"/> as gateway middleware using the given provider pool.
    /// </summary>
    public static IServiceCollection AddLlmFallbackMiddleware(
        this IServiceCollection services,
        IFallbackProviderPool pool)
    {
        services.AddSingleton(pool);
        services.AddSingleton<LlmGatewayMiddleware, LlmFallbackMiddleware>();
        return services;
    }
}
