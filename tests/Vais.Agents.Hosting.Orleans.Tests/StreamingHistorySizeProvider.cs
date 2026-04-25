// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Runtime.CompilerServices;

namespace Vais.Agents.Hosting.Orleans.Tests;

/// <summary>
/// Test <see cref="ICompletionProvider"/> / <see cref="IStreamingCompletionProvider"/>
/// that replies with the observed <see cref="CompletionRequest.History"/> size.
/// Streaming-capable variant of <c>HistorySizeProvider</c>; replaces it in the
/// cluster fixture so streaming tests can exercise the full grain path.
/// </summary>
public sealed class StreamingHistorySizeProvider : ICompletionProvider, IStreamingCompletionProvider
{
    public string ProviderName => "streaming-history-size-fake";

    public Task<CompletionResponse> CompleteAsync(CompletionRequest request, CancellationToken cancellationToken = default)
        => Task.FromResult(new CompletionResponse(
            Text: $"history-size={request.History.Count}",
            ModelId: "fake-model",
            PromptTokens: request.History.Count,
            CompletionTokens: 1));

    public async IAsyncEnumerable<CompletionUpdate> StreamAsync(
        CompletionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        yield return new CompletionUpdate(
            TextDelta: $"history-size={request.History.Count}",
            ModelId: "fake-model",
            PromptTokens: request.History.Count,
            CompletionTokens: 1);
    }
}
