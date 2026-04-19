// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Core.Tests;

/// <summary>
/// Test double: records every request, returns a canned response. Zero network.
/// </summary>
internal sealed class FakeCompletionProvider : ICompletionProvider
{
    private readonly Func<CompletionRequest, CompletionResponse> _respond;

    public FakeCompletionProvider(Func<CompletionRequest, CompletionResponse>? respond = null)
    {
        _respond = respond ?? (_ => new CompletionResponse("ok"));
    }

    public List<CompletionRequest> Received { get; } = new();

    public string ProviderName => "Fake";

    public Task<CompletionResponse> CompleteAsync(CompletionRequest request, CancellationToken cancellationToken = default)
    {
        Received.Add(request);
        return Task.FromResult(_respond(request));
    }
}
