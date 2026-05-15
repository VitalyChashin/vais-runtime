// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Persistence.VectorData;

/// <summary>
/// Configuration for <see cref="KnowledgeRetrievalFilter"/>.
/// </summary>
public sealed record KnowledgeRetrievalOptions
{
    /// <summary>
    /// Maximum number of chunks to request from the retriever. Default: 5.
    /// </summary>
    public int TopK { get; init; } = 5;

    /// <summary>
    /// Template for the retrieved-context block that gets appended to the request's
    /// system prompt. The token <c>{chunks}</c> is replaced with the concatenated
    /// chunk texts (separated by <see cref="ChunkSeparator"/>). Default:
    /// <c>"Relevant context:\n{chunks}"</c>.
    /// </summary>
    public string Template { get; init; } = "Relevant context:\n{chunks}";

    /// <summary>
    /// Separator placed between chunks when filling the <c>{chunks}</c> slot in
    /// <see cref="Template"/>. Default: <c>"\n---\n"</c>.
    /// </summary>
    public string ChunkSeparator { get; init; } = "\n---\n";

    /// <summary>
    /// Section id emitted by <c>KnowledgeRetrievalContextProvider</c>. Default: <c>"retrieval.docs"</c>.
    /// Override when wiring multiple <see cref="IKnowledgeRetriever"/>s to the same agent so
    /// the section resolver can distinguish them (e.g. <c>"retrieval.support_kb"</c>,
    /// <c>"retrieval.product_specs"</c>).
    /// </summary>
    public string SectionId { get; init; } = "retrieval.docs";
}
