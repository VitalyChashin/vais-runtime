// Copyright (c) 2026 VAIS2 Platform contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais2.Agents.Hosting.Orleans.Tests;

/// <summary>
/// Test <see cref="ICompletionProvider"/> that replies with the observed
/// <see cref="CompletionRequest.History"/> size. Useful for asserting that grain
/// reactivation rehydrated state correctly — after a grain deactivates and the next
/// turn fires, the provider sees the full pre-deactivation history plus the new user
/// turn.
/// </summary>
public sealed class HistorySizeProvider : ICompletionProvider
{
    public string ProviderName => "history-size-fake";

    public Task<CompletionResponse> CompleteAsync(CompletionRequest request, CancellationToken cancellationToken = default)
        => Task.FromResult(new CompletionResponse(
            Text: $"history-size={request.History.Count}",
            ModelId: "fake-model",
            PromptTokens: request.History.Count,
            CompletionTokens: 1));
}
