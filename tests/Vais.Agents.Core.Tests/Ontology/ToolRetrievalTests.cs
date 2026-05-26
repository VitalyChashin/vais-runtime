// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Microsoft.Extensions.AI;
using Vais.Agents.Control.Manifests;
using Xunit;

namespace Vais.Agents.Core.Tests.Ontology;

/// <summary>
/// C1-10 verify gate: lexical recall + semantic rerank.
/// </summary>
public sealed class ToolRetrievalTests
{
    private static readonly ToolDescriptor[] Tools =
    [
        new("fetch_url",       "Fetch a URL and return the response body."),
        new("list_files",      "List files in a directory on the local filesystem."),
        new("query_database",  "Run a read-only SQL query against the connected database."),
        new("send_email",      "Send an email via SMTP. Requires recipient + subject + body."),
        new("delete_file",     "Delete a file on the local filesystem. Destructive."),
    ];

    // ── lexical recall (always-on default) ────────────────────────────────────

    [Fact]
    public async Task Lexical_FindsToolByNameMatch_HighestScored()
    {
        var retriever = new LexicalToolRetriever();
        var result = await retriever.RetrieveAsync("fetch", Tools, topK: 3);

        result.Should().NotBeEmpty();
        result[0].Tool.Name.Should().Be("fetch_url", "name match has weight ×3");
    }

    [Fact]
    public async Task Lexical_FindsToolByDescriptionMatch()
    {
        var retriever = new LexicalToolRetriever();
        var result = await retriever.RetrieveAsync("destructive", Tools, topK: 5);

        result.Should().Contain(s => s.Tool.Name == "delete_file");
    }

    [Fact]
    public async Task Lexical_TopKHonored()
    {
        var retriever = new LexicalToolRetriever();
        var result = await retriever.RetrieveAsync("file directory database email body", Tools, topK: 2);

        result.Should().HaveCount(2);
    }

    [Fact]
    public async Task Lexical_RanksMultiTermMatchesAboveSingle()
    {
        // "file" alone matches list_files + delete_file. "file local filesystem" matches
        // both but list_files and delete_file each get more terms.
        var retriever = new LexicalToolRetriever();
        var result = await retriever.RetrieveAsync("file local filesystem", Tools, topK: 5);

        result.Should().NotBeEmpty();
        // Both list_files and delete_file should beat unrelated tools.
        var top2 = result.Take(2).Select(s => s.Tool.Name).ToList();
        top2.Should().Contain(["list_files", "delete_file"]);
    }

    [Fact]
    public async Task Lexical_EmptyQueryThrows()
    {
        var retriever = new LexicalToolRetriever();
        await FluentActions.Invoking(async () => await retriever.RetrieveAsync("", Tools, 5))
            .Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Lexical_ZeroTopKReturnsEmpty()
    {
        var retriever = new LexicalToolRetriever();
        var result = await retriever.RetrieveAsync("fetch", Tools, topK: 0);
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task Lexical_NonMatchingQueryReturnsEmpty()
    {
        var retriever = new LexicalToolRetriever();
        var result = await retriever.RetrieveAsync("zzzqqqxxxyyy", Tools, topK: 5);
        result.Should().BeEmpty("no candidate scored above zero");
    }

    [Fact]
    public async Task Lexical_TagWeightBeatsDescriptionWhenCatalogTagsProvided()
    {
        // Provide tags for delete_file that include the query term — should outrank a
        // description-only match.
        var artifact = new DomainOntologyArtifact
        {
            Tools = new Dictionary<string, DomainConcept>
            {
                ["delete_file"] = new() { Tags = ["risk:Destructive", "category:dangerous"] },
            },
        };
        var catalog = new DomainOntologyCatalog(artifact, Tools.Select(t => t.Name).ToList());
        var retriever = LexicalToolRetriever.ForCatalog(catalog);

        var result = await retriever.RetrieveAsync("dangerous", Tools, topK: 5);

        result[0].Tool.Name.Should().Be("delete_file",
            "the catalog tag 'dangerous' matched at the tag-weight tier (×2)");
    }

    // ── semantic rerank (opt-in, deterministic with stub) ─────────────────────

    [Fact]
    public async Task Semantic_StubEmbedderReordersDeterministically()
    {
        // Lexical query "file" matches both list_files and delete_file (description hits) — so
        // both make recall. Then we pin embeddings so the query vector lies right next to
        // delete_file and far from list_files, forcing the semantic stage to reorder
        // (delete_file rises above list_files even though lexical alone would not prefer it).
        var stub = new StubEmbedder();
        stub.PinVector("file", [0.0f, 1.0f]);
        stub.PinVector("delete_file: Delete a file on the local filesystem. Destructive.", [0.1f, 0.99f]);
        stub.PinVector("list_files: List files in a directory on the local filesystem.", [0.99f, 0.1f]);

        var lexical = new LexicalToolRetriever();
        var semantic = new SemanticToolRetriever(lexical, stub);

        var recall = await lexical.RetrieveAsync("file", new[] { Tools[1], Tools[4] }, topK: 2);
        recall.Should().HaveCount(2, "both should make lexical recall on 'file'");

        var reranked = await semantic.RetrieveAsync("file", new[] { Tools[1], Tools[4] }, topK: 2);
        reranked[0].Tool.Name.Should().Be("delete_file",
            "pinned vectors give delete_file the higher cosine similarity to 'file'");
    }

    [Fact]
    public async Task Semantic_TopKHonoredAfterRerank()
    {
        var stub = new StubEmbedder();
        var semantic = new SemanticToolRetriever(new LexicalToolRetriever(), stub);

        var result = await semantic.RetrieveAsync("file", Tools, topK: 1);

        result.Should().HaveCount(1);
    }

    [Fact]
    public async Task Semantic_DegradesGracefullyOnEmptyRecall()
    {
        var stub = new StubEmbedder();
        var semantic = new SemanticToolRetriever(new LexicalToolRetriever(), stub);

        var result = await semantic.RetrieveAsync("zzzqqqxxx", Tools, topK: 5);

        result.Should().BeEmpty("nothing to rerank when recall is empty");
    }

    [Fact]
    public async Task Semantic_SingleRecallShortCircuitsEmbeddingCall()
    {
        var stub = new StubEmbedder();
        var semantic = new SemanticToolRetriever(new LexicalToolRetriever(), stub);

        var result = await semantic.RetrieveAsync("fetch", Tools, topK: 5);

        // Lexical finds fetch_url uniquely; rerank with one item just passes through.
        result.Should().ContainSingle().Which.Tool.Name.Should().Be("fetch_url");
        stub.QueryEmbedCalls.Should().Be(0, "single-recall short-circuit avoids the embed roundtrip");
    }

    // ── stub IEmbeddingGenerator ──────────────────────────────────────────────

    private sealed class StubEmbedder : IEmbeddingGenerator<string, Embedding<float>>
    {
        private readonly Dictionary<string, float[]> _pinned = new(StringComparer.Ordinal);
        public int QueryEmbedCalls { get; private set; }

        public void PinVector(string text, float[] vector) => _pinned[text] = vector;

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            QueryEmbedCalls++;
            var generated = new GeneratedEmbeddings<Embedding<float>>();
            foreach (var v in values) generated.Add(new Embedding<float>(VectorFor(v)));
            return Task.FromResult(generated);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }

        private ReadOnlyMemory<float> VectorFor(string input)
        {
            if (_pinned.TryGetValue(input, out var pinned)) return pinned;
            // Fallback: deterministic hash-derived 2-D vector.
            var h = input.GetHashCode();
            var x = ((h & 0xFFFF) / 65535f) * 2f - 1f;
            var y = (((h >> 16) & 0xFFFF) / 65535f) * 2f - 1f;
            return new float[] { x, y };
        }
    }
}
