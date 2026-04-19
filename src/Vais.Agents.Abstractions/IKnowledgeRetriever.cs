// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents;

/// <summary>
/// A source of retrieved context for RAG-style agent augmentation. Given a text query,
/// returns the top-K most relevant <see cref="KnowledgeChunk"/>s. Implementations range
/// from vector-store similarity search to external search APIs.
/// </summary>
/// <remarks>
/// This abstraction is deliberately tiny. Everything about embedding, ranking, or storage
/// is an implementation concern. Consumers typically wire a retriever into the agent turn
/// pipeline via a retrieval-augmenting <see cref="IAgentFilter"/>.
/// </remarks>
public interface IKnowledgeRetriever
{
    /// <summary>
    /// Retrieve the top <paramref name="topK"/> chunks most relevant to <paramref name="query"/>.
    /// </summary>
    /// <param name="query">The text query. Non-null.</param>
    /// <param name="topK">Requested number of results. Implementations may return fewer; must not return more.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<KnowledgeChunk>> RetrieveAsync(
        string query,
        int topK = 5,
        CancellationToken cancellationToken = default);
}
