// Copyright (c) 2026 VAIS2 Platform contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais2.Agents.Persistence.Redis.Tests;

/// <summary>
/// Test <see cref="ICompletionProvider"/> that replies with the observed
/// <see cref="CompletionRequest.History"/> size. Duplicated intentionally from
/// the Orleans test project so the Redis tests don't cross-depend.
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
