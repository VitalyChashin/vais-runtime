// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Vais.Agents;
using Vais.Agents.Core;
using Vais.Agents.Persistence.VectorData;

// -----------------------------------------------------------------------------
// ContextProviderRag — wires KnowledgeRetrievalContextProvider with a mock
// IKnowledgeRetriever. The retriever returns pre-canned chunks keyed by the
// latest user turn; the provider injects them as a SystemPromptAddendum.
// Drives one turn through a recording fake provider so the augmented system
// prompt is observable.
// -----------------------------------------------------------------------------

var retriever = new MockRetriever(new Dictionary<string, KnowledgeChunk[]>
{
    ["return policy"] = new[]
    {
        new KnowledgeChunk("Returns accepted within 30 days of delivery.", Id: "doc-1"),
        new KnowledgeChunk("Original receipt required for all returns.", Id: "doc-2"),
    },
});

var ragProvider = new KnowledgeRetrievalContextProvider(
    retriever,
    options: new KnowledgeRetrievalOptions
    {
        TopK = 3,
        Template = "Relevant policy context:\n{chunks}",
    });

var provider = new RecordingFakeProvider();

var agent = new StatefulAiAgent(
    provider,
    new StatefulAgentOptions
    {
        ContextProviders = new IContextProvider[] { ragProvider },
        SystemPrompt = "You are a support assistant. Quote policy exactly.",
    });

var reply = await agent.AskAsync("What's your return policy?");

Console.WriteLine("=== augmented SystemPrompt seen by the provider ===");
Console.WriteLine(provider.LastRequest!.SystemPrompt);
Console.WriteLine();
Console.WriteLine($"=== reply ===\n{reply}");

sealed class MockRetriever(Dictionary<string, KnowledgeChunk[]> canned) : IKnowledgeRetriever
{
    public Task<IReadOnlyList<KnowledgeChunk>> RetrieveAsync(string query, int topK = 5, CancellationToken ct = default)
    {
        foreach (var (needle, chunks) in canned)
            if (query.Contains(needle, StringComparison.OrdinalIgnoreCase))
                return Task.FromResult<IReadOnlyList<KnowledgeChunk>>(chunks.Take(topK).ToList());
        return Task.FromResult<IReadOnlyList<KnowledgeChunk>>(Array.Empty<KnowledgeChunk>());
    }
}

sealed class RecordingFakeProvider : ICompletionProvider
{
    public CompletionRequest? LastRequest { get; private set; }
    public string ProviderName => "recording-fake";
    public Task<CompletionResponse> CompleteAsync(CompletionRequest request, CancellationToken ct = default)
    {
        LastRequest = request;
        return Task.FromResult(new CompletionResponse(
            "Returns are accepted within 30 days of delivery with your original receipt.",
            ModelId: "fake-model"));
    }
}
