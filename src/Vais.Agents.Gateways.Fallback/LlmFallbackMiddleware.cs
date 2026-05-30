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
public sealed class LlmFallbackMiddleware : LlmGatewayMiddleware
{
    private readonly IFallbackProviderPool _pool;
    private readonly IAgentEventBus? _eventBus;
    private readonly IAgentContextAccessor? _contextAccessor;

    /// <summary>
    /// Construct the fallback middleware over <paramref name="pool"/>. When an
    /// <see cref="IAgentEventBus"/> is supplied (resolved from DI in the runtime), each
    /// fall-through to the next provider publishes an <see cref="LlmFallbackEngaged"/> event so a
    /// recovered fallback is observable rather than silent.
    /// </summary>
    public LlmFallbackMiddleware(
        IFallbackProviderPool pool,
        IAgentEventBus? eventBus = null,
        IAgentContextAccessor? contextAccessor = null)
    {
        _pool = pool;
        _eventBus = eventBus;
        _contextAccessor = contextAccessor;
    }

    private ValueTask EmitFallbackAsync(
        int fromIndex, int toIndex, ICompletionProvider from, ICompletionProvider to, Exception failure, CancellationToken ct)
    {
        if (_eventBus is null)
        {
            return ValueTask.CompletedTask;
        }

        var ctx = _contextAccessor?.Current ?? AgentContext.Empty;
        return _eventBus.PublishAsync(
            new LlmFallbackEngaged(
                DateTimeOffset.UtcNow, ctx, fromIndex, toIndex, from.ProviderName, to.ProviderName, failure.GetType().Name),
            ct);
    }

    /// <inheritdoc/>
    protected override async Task<CompletionResponse> InvokeAsync(
        CompletionRequest request,
        Func<CompletionRequest, CancellationToken, Task<CompletionResponse>> next,
        CancellationToken cancellationToken)
    {
        var providers = _pool.GetProviders();
        Exception? last = null;

        for (var i = 0; i < providers.Count; i++)
        {
            try
            {
                return await providers[i].CompleteAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                last = ex;
                if (i + 1 < providers.Count)
                {
                    await EmitFallbackAsync(i, i + 1, providers[i], providers[i + 1], ex, cancellationToken).ConfigureAwait(false);
                }
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
        var providers = _pool.GetProviders();
        IAsyncEnumerator<CompletionUpdate>? committed = null;
        CompletionUpdate firstDelta = default!;
        Exception? last = null;

        // Phase 1: find the first provider that successfully delivers at least one delta.
        // yield return is NOT used here — C# disallows yield inside try/catch.
        for (var i = 0; i < providers.Count; i++)
        {
            var provider = providers[i];
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
                if (i + 1 < providers.Count)
                {
                    await EmitFallbackAsync(i, i + 1, provider, providers[i + 1], ex, cancellationToken).ConfigureAwait(false);
                }
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

    /// <summary>
    /// Registers <see cref="LlmFallbackMiddleware"/> as a named factory under the key
    /// <c>"Fallback"</c>. The middleware resolves <see cref="IFallbackProviderPool"/> from DI
    /// at agent activation time; the pool must be registered separately.
    /// </summary>
    public static IServiceCollection AddNamedLlmGatewayMiddleware_Fallback(
        this IServiceCollection services)
        => services.AddSingleton(
            sp => new NamedLlmGatewayMiddlewareRegistration(
                "Fallback",
                (_, _) => new LlmFallbackMiddleware(
                    sp.GetRequiredService<IFallbackProviderPool>(),
                    sp.GetService<IAgentEventBus>(),
                    sp.GetService<IAgentContextAccessor>())));
}
