// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.Extensions.AI;
using Microsoft.Extensions.VectorData;

namespace Vais.Agents.Persistence.VectorData;

/// <summary>
/// An <see cref="IKnowledgeRetriever"/> that looks up context via a
/// <see cref="VectorStoreCollection{TKey, TRecord}"/>. The query is embedded with the
/// supplied <see cref="IEmbeddingGenerator{TInput, TEmbedding}"/>, then the collection's
/// <c>SearchAsync</c> returns the top-K nearest records, which a consumer-provided
/// projection converts to <see cref="KnowledgeChunk"/>.
/// </summary>
/// <remarks>
/// <para>
/// The type parameters <typeparamref name="TKey"/> and <typeparamref name="TRecord"/>
/// are whatever the chosen <c>Microsoft.Extensions.VectorData</c> connector requires
/// (Azure AI Search, Qdrant, pgvector, Redis, in-memory, …). Consumers pick the
/// connector, decorate their record type with the <c>[VectorStoreRecordKey/Data/Vector]</c>
/// attributes, and hand the resulting collection in.
/// </para>
/// <para>
/// The projection is a <c>Func&lt;TRecord, KnowledgeChunk&gt;</c>; the retriever then
/// annotates each chunk with the search result's score (higher-is-better by
/// <c>Microsoft.Extensions.VectorData</c> convention).
/// </para>
/// </remarks>
public sealed class VectorStoreKnowledgeRetriever<TKey, TRecord> : IKnowledgeRetriever
    where TKey : notnull
    where TRecord : class
{
    private readonly VectorStoreCollection<TKey, TRecord> _collection;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddings;
    private readonly Func<TRecord, KnowledgeChunk> _toChunk;

    /// <summary>
    /// Create a retriever over an existing vector-store collection.
    /// </summary>
    /// <param name="collection">The vector-store collection to query.</param>
    /// <param name="embeddings">Generator used to embed the incoming text query.</param>
    /// <param name="toChunk">Projection from a record to a <see cref="KnowledgeChunk"/>; the retriever overrides <see cref="KnowledgeChunk.Score"/> with the search-result score.</param>
    public VectorStoreKnowledgeRetriever(
        VectorStoreCollection<TKey, TRecord> collection,
        IEmbeddingGenerator<string, Embedding<float>> embeddings,
        Func<TRecord, KnowledgeChunk> toChunk)
    {
        ArgumentNullException.ThrowIfNull(collection);
        ArgumentNullException.ThrowIfNull(embeddings);
        ArgumentNullException.ThrowIfNull(toChunk);
        _collection = collection;
        _embeddings = embeddings;
        _toChunk = toChunk;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<KnowledgeChunk>> RetrieveAsync(
        string query,
        int topK = 5,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(query);
        if (topK <= 0)
        {
            return Array.Empty<KnowledgeChunk>();
        }

        var embedding = await _embeddings
            .GenerateAsync(query, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var chunks = new List<KnowledgeChunk>();
        await foreach (var result in _collection
            .SearchAsync(embedding.Vector, top: topK, options: null, cancellationToken)
            .ConfigureAwait(false))
        {
            var chunk = _toChunk(result.Record) with { Score = (float?)result.Score };
            chunks.Add(chunk);
        }
        return chunks;
    }
}
