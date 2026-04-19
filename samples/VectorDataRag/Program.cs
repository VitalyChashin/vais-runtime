// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.VectorData;
using Microsoft.SemanticKernel.Connectors.InMemory;
using Vais.Agents;
using Vais.Agents.Core;
using Vais.Agents.Persistence.VectorData;

// -----------------------------------------------------------------------------
// VectorDataRag — ingests three short docs into SK's InMemory VectorStore
// (via Microsoft.Extensions.VectorData), wires VectorStoreKnowledgeRetriever +
// KnowledgeRetrievalContextProvider, drives a turn through a scripted provider
// so the augmented system prompt (retrieved context joined to the base) is
// observable.
//
// Uses a deterministic SHA256-backed fake embedder — exact-string matches
// score 1.0, different strings score uncorrelated. Don't use in production;
// wire a real IEmbeddingGenerator.
// -----------------------------------------------------------------------------

var store = new InMemoryVectorStore();
var collection = store.GetCollection<string, Doc>("docs");
await collection.EnsureCollectionExistsAsync();

var embedder = new HashEmbeddingGenerator();

// Ingest.
foreach (var (id, text) in new[]
{
    ("doc-1", "Returns accepted within 30 days of delivery."),
    ("doc-2", "Original receipt required for all returns."),
    ("doc-3", "Refunds processed within 5 business days."),
})
{
    var emb = await embedder.GenerateAsync(new[] { text });
    await collection.UpsertAsync(new Doc { Id = id, Text = text, Embedding = emb[0].Vector });
}

// Retriever + provider.
var retriever = new VectorStoreKnowledgeRetriever<string, Doc>(
    collection,
    embedder,
    toChunk: r => new KnowledgeChunk(r.Text, Id: r.Id));

var ragProvider = new KnowledgeRetrievalContextProvider(
    retriever,
    options: new KnowledgeRetrievalOptions { TopK = 2, Template = "Relevant policy:\n{chunks}" });

// Drive the agent.
var provider = new RecordingFakeProvider();
var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
{
    ContextProviders = new IContextProvider[] { ragProvider },
    SystemPrompt = "Quote policy exactly.",
});

// Ask with exact-text query so cosine similarity with the ingested doc is 1.0.
var reply = await agent.AskAsync("Returns accepted within 30 days of delivery.");
Console.WriteLine("=== augmented SystemPrompt ===");
Console.WriteLine(provider.LastRequest!.SystemPrompt);
Console.WriteLine();
Console.WriteLine($"=== reply ===\n{reply}");

sealed class Doc
{
    [VectorStoreKey] public required string Id { get; init; }
    [VectorStoreData] public required string Text { get; init; }
    [VectorStoreVector(8)] public required ReadOnlyMemory<float> Embedding { get; init; }
}

sealed class RecordingFakeProvider : ICompletionProvider
{
    public CompletionRequest? LastRequest { get; private set; }
    public string ProviderName => "recording";
    public Task<CompletionResponse> CompleteAsync(CompletionRequest request, CancellationToken ct = default)
    {
        LastRequest = request;
        return Task.FromResult(new CompletionResponse(
            "Yes — returns accepted within 30 days.",
            ModelId: "fake-model"));
    }
}

sealed class HashEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    private const int Dimensions = 8;
    public EmbeddingGeneratorMetadata Metadata { get; } =
        new(providerName: "hash-fake", defaultModelId: "hash-sha256-8d");

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken ct = default)
        => Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(
            values.Select(v => new Embedding<float>(Compute(v))).ToArray()));

    public object? GetService(Type serviceType, object? serviceKey = null)
        => serviceType == typeof(EmbeddingGeneratorMetadata) ? Metadata : null;

    public void Dispose() { }

    static ReadOnlyMemory<float> Compute(string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        var floats = new float[Dimensions];
        for (var i = 0; i < Dimensions; i++)
        {
            var raw = (short)((bytes[i * 2] << 8) | bytes[i * 2 + 1]);
            floats[i] = raw / 32768f;
        }
        var norm = MathF.Sqrt(floats.Sum(f => f * f));
        if (norm > 0f)
            for (var i = 0; i < Dimensions; i++) floats[i] /= norm;
        return floats;
    }
}
