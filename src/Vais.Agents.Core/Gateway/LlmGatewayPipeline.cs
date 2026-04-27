// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Runtime.CompilerServices;
using System.Text;

namespace Vais.Agents.Core;

/// <summary>
/// Builds and executes an <see cref="LlmGatewayMiddleware"/> chain against a
/// standalone provider. Used by transport endpoints (e.g. the OpenAI-compatible
/// gateway) that invoke the chain outside of a <see cref="StatefulAiAgent"/>.
/// </summary>
public static class LlmGatewayPipeline
{
    /// <summary>
    /// Invokes <paramref name="request"/> through the <paramref name="middleware"/>
    /// chain and returns the provider's non-streaming response.
    /// </summary>
    public static Task<CompletionResponse> InvokeAsync(
        CompletionRequest request,
        ICompletionProvider provider,
        IEnumerable<LlmGatewayMiddleware> middleware,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(middleware);

        var filters = middleware as IReadOnlyList<LlmGatewayMiddleware> ?? [.. middleware];

        Func<CompletionRequest, CancellationToken, Task<CompletionResponse>> next =
            (req, ct) => provider.CompleteAsync(req, ct);

        for (var i = filters.Count - 1; i >= 0; i--)
        {
            var filter = (IAgentFilter)filters[i];
            var inner = next;
            next = (req, ct) => filter.InvokeAsync(req, inner, ct);
        }

        return next(request, cancellationToken);
    }

    /// <summary>
    /// Invokes <paramref name="request"/> through the streaming
    /// <paramref name="middleware"/> chain and yields <see cref="CompletionUpdate"/>s
    /// as the provider produces them. Fires <c>OnStreamDeltaAsync</c> on each
    /// middleware per delta and <c>OnStreamCompleteAsync</c> once after the stream ends.
    /// </summary>
    public static async IAsyncEnumerable<CompletionUpdate> StreamAsync(
        CompletionRequest request,
        IStreamingCompletionProvider provider,
        IEnumerable<LlmGatewayMiddleware> middleware,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(middleware);

        var filters = middleware as IReadOnlyList<LlmGatewayMiddleware> ?? [.. middleware];

        Func<CompletionRequest, CancellationToken, IAsyncEnumerable<CompletionUpdate>> next =
            (req, ct) => provider.StreamAsync(req, ct);

        for (var i = filters.Count - 1; i >= 0; i--)
        {
            var filter = (IStreamingAgentFilter)filters[i];
            var inner = next;
            next = (req, ct) => filter.InvokeAsync(req, inner, ct);
        }

        var stream = next(request, cancellationToken);

        var textBuilder = new StringBuilder();
        string? modelId = null;
        int? promptTokens = null;
        int? completionTokens = null;

        await foreach (var update in stream.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            var processed = update;
            foreach (var filter in filters)
            {
                processed = await ((IStreamingAgentFilter)filter)
                    .OnStreamDeltaAsync(processed, cancellationToken).ConfigureAwait(false);
            }

            if (processed.TextDelta.Length > 0)
                textBuilder.Append(processed.TextDelta);
            if (processed.ModelId is not null)
                modelId = processed.ModelId;
            if (processed.PromptTokens is not null)
                promptTokens = processed.PromptTokens;
            if (processed.CompletionTokens is not null)
                completionTokens = processed.CompletionTokens;

            yield return processed;
        }

        var final = new CompletionResponse(textBuilder.ToString(), modelId, promptTokens, completionTokens);
        foreach (var filter in filters)
        {
            await ((IStreamingAgentFilter)filter)
                .OnStreamCompleteAsync(final, cancellationToken).ConfigureAwait(false);
        }
    }
}
