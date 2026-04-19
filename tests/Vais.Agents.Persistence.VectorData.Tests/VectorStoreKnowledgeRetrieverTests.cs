// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Microsoft.SemanticKernel.Connectors.InMemory;
using Xunit;

namespace Vais.Agents.Persistence.VectorData.Tests;

public sealed class VectorStoreKnowledgeRetrieverTests
{
    [Fact]
    public async Task Retrieves_Chunks_From_InMemory_Vector_Store()
    {
        // Uses exact-match query text so the SHA256-based fake embedding generator
        // produces the same vector for query and stored record — cosine similarity = 1.
        // The retriever's contract we're verifying here is "query the store, return top-K
        // with score", not "fake embedder understands natural language".
        const string target = "Semantic Kernel is a Microsoft AI orchestration framework.";
        var (retriever, _) = await BuildAsync(new[]
        {
            ("doc-1", target),
            ("doc-2", "The capital of France is Paris."),
            ("doc-3", "Apache-2.0 is a permissive open-source licence."),
        });

        var chunks = await retriever.RetrieveAsync(target, topK: 1);

        chunks.Should().HaveCount(1);
        chunks[0].Text.Should().Be(target);
        chunks[0].Id.Should().Be("doc-1");
        chunks[0].Score.Should().NotBeNull();
    }

    [Fact]
    public async Task Projection_Receives_Original_Record_And_Output_Is_Annotated_With_Score()
    {
        var store = new InMemoryVectorStore();
        var collection = store.GetCollection<string, TestDocumentRecord>("docs");
        await collection.EnsureCollectionExistsAsync();
        await collection.UpsertAsync(new TestDocumentRecord
        {
            Id = "only",
            Text = "unique document",
            Vector = HashEmbeddingGenerator.Compute("unique document"),
        });

        var retriever = new VectorStoreKnowledgeRetriever<string, TestDocumentRecord>(
            collection,
            new HashEmbeddingGenerator(),
            record => new KnowledgeChunk(Text: $"projected:{record.Text}", Id: record.Id));

        var chunks = await retriever.RetrieveAsync("unique document", topK: 3);

        chunks.Should().ContainSingle();
        chunks[0].Text.Should().Be("projected:unique document");
        chunks[0].Id.Should().Be("only");
        chunks[0].Score.Should().NotBeNull("retriever must override the projection's Score with the search result score");
    }

    [Fact]
    public async Task Respects_TopK_Bound()
    {
        var (retriever, _) = await BuildAsync(new[]
        {
            ("a", "alpha"),
            ("b", "beta"),
            ("c", "gamma"),
            ("d", "delta"),
            ("e", "epsilon"),
        });

        var chunks = await retriever.RetrieveAsync("alpha beta gamma delta epsilon", topK: 2);

        chunks.Count.Should().BeLessThanOrEqualTo(2);
    }

    [Fact]
    public async Task Zero_Or_Negative_TopK_Returns_Empty()
    {
        var (retriever, _) = await BuildAsync(new[] { ("a", "alpha") });

        (await retriever.RetrieveAsync("alpha", topK: 0)).Should().BeEmpty();
        (await retriever.RetrieveAsync("alpha", topK: -1)).Should().BeEmpty();
    }

    private static async Task<(VectorStoreKnowledgeRetriever<string, TestDocumentRecord> Retriever, InMemoryVectorStore Store)> BuildAsync(
        IEnumerable<(string Id, string Text)> docs)
    {
        var store = new InMemoryVectorStore();
        var collection = store.GetCollection<string, TestDocumentRecord>("docs");
        await collection.EnsureCollectionExistsAsync();

        foreach (var (id, text) in docs)
        {
            await collection.UpsertAsync(new TestDocumentRecord
            {
                Id = id,
                Text = text,
                Vector = HashEmbeddingGenerator.Compute(text),
            });
        }

        var retriever = new VectorStoreKnowledgeRetriever<string, TestDocumentRecord>(
            collection,
            new HashEmbeddingGenerator(),
            record => new KnowledgeChunk(Text: record.Text, Id: record.Id));
        return (retriever, store);
    }
}
