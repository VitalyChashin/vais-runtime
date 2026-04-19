// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.CrossHostTests;

/// <summary>
/// Deterministic <see cref="ICompletionProvider"/> that replies with the observed
/// <see cref="CompletionRequest.History"/> size. Identical to the homonymous helpers
/// living in the Hosting.Orleans / Persistence.Redis / Persistence.Postgres test
/// projects — duplicated here rather than shared so each test project stays
/// self-contained and this project's assembly has no inter-test-assembly coupling.
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
