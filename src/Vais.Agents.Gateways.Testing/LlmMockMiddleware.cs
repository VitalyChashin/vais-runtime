// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Runtime.CompilerServices;

namespace Vais.Agents.Gateways.Testing;

/// <summary>
/// Gateway middleware that intercepts LLM calls and returns pre-queued <see cref="CompletionResponse"/> objects.
/// Useful in unit tests to avoid real LLM providers while exercising agent logic end-to-end.
/// </summary>
/// <remarks>
/// Dequeues one response per call. Throws <see cref="InvalidOperationException"/> when the queue is exhausted.
/// On the streaming path, converts the queued response to a single <see cref="CompletionUpdate"/> delta.
/// </remarks>
public sealed class LlmMockMiddleware : LlmGatewayMiddleware
{
    private readonly Queue<CompletionResponse> _responses;

    /// <summary>
    /// Initializes a new instance with the given sequence of canned responses.
    /// </summary>
    public LlmMockMiddleware(params CompletionResponse[] responses)
        => _responses = new Queue<CompletionResponse>(responses);

    /// <inheritdoc/>
    protected override Task<CompletionResponse> InvokeAsync(
        CompletionRequest request,
        Func<CompletionRequest, CancellationToken, Task<CompletionResponse>> next,
        CancellationToken cancellationToken)
    {
        if (!_responses.TryDequeue(out var response))
            throw new InvalidOperationException("LlmMockMiddleware: no more queued responses.");
        return Task.FromResult(response);
    }

    /// <inheritdoc/>
    protected override IAsyncEnumerable<CompletionUpdate> InvokeStreamAsync(
        CompletionRequest request,
        Func<CompletionRequest, CancellationToken, IAsyncEnumerable<CompletionUpdate>> next,
        CancellationToken cancellationToken)
    {
        if (!_responses.TryDequeue(out var response))
            throw new InvalidOperationException("LlmMockMiddleware: no more queued responses.");
        return YieldOneAsync(response, cancellationToken);
    }

#pragma warning disable CS1998 // Async method lacks 'await' — iterator is synchronous by design.
    private static async IAsyncEnumerable<CompletionUpdate> YieldOneAsync(
        CompletionResponse response,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return new CompletionUpdate(response.Text, response.ModelId,
            response.PromptTokens, response.CompletionTokens, response.ToolCalls);
    }
#pragma warning restore CS1998
}
