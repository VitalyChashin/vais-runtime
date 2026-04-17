// Copyright (c) 2026 VAIS2 Platform contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais2.Agents.Observability.Tests;

/// <summary>
/// Test double: returns a canned response. Zero network.
/// </summary>
internal sealed class FakeCompletionProvider : ICompletionProvider
{
    private readonly Func<CompletionRequest, CompletionResponse> _respond;

    public FakeCompletionProvider(Func<CompletionRequest, CompletionResponse>? respond = null)
    {
        _respond = respond ?? (_ => new CompletionResponse("ok", "gpt-fake", 5, 7));
    }

    public string ProviderName => "fake";

    public Task<CompletionResponse> CompleteAsync(CompletionRequest request, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_respond(request));
    }
}
