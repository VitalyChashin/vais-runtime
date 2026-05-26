// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.Extensions.AI;

namespace Vais.Agents.Control.Manifests;

/// <summary>
/// One scored candidate returned by <see cref="IToolRetriever"/>. Higher
/// <see cref="Score"/> = better match for the query.
/// </summary>
public sealed record ScoredTool(ToolDescriptor Tool, double Score);

/// <summary>
/// Selects the top-K tools from a candidate pool given a free-text query. Implementations
/// compose: a lexical recall retriever can be the inner stage of a semantic-rerank decorator,
/// and a deployer-supplied <see cref="IToolClassifier"/> can post-process either output.
/// </summary>
/// <remarks>
/// Plan C1-10: lexical recall is always-on; semantic rerank is opt-in behind a registered
/// <see cref="IEmbeddingGenerator{TInput, TEmbedding}"/>; classifier is hook-only in C1.
/// </remarks>
public interface IToolRetriever
{
    /// <summary>Retrieve the top <paramref name="topK"/> candidates for <paramref name="query"/>.</summary>
    /// <param name="query">Free-text intent (typically the agent's most recent user turn or a goal sentence).</param>
    /// <param name="candidates">The candidate pool to score and rank.</param>
    /// <param name="topK">Maximum number of results to return. Must be &gt; 0.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Up to <paramref name="topK"/> scored candidates, sorted by <see cref="ScoredTool.Score"/> descending.</returns>
    ValueTask<IReadOnlyList<ScoredTool>> RetrieveAsync(
        string query,
        IReadOnlyList<ToolDescriptor> candidates,
        int topK,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Deployer-supplied post-processor that can re-rank or filter the retriever output (e.g. an
/// LLM-backed classifier). Hook only in C1 — no default impl ships; deployers wrap a retriever
/// chain manually and invoke the classifier between stages.
/// </summary>
public interface IToolClassifier
{
    /// <summary>Re-rank or filter <paramref name="candidates"/> for <paramref name="query"/>.</summary>
    ValueTask<IReadOnlyList<ScoredTool>> ClassifyAsync(
        string query,
        IReadOnlyList<ScoredTool> candidates,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Always-on lexical recall over a tool catalog. Scores by weighted query-term matches across
/// tool name (×3), tags (×2), and description (×1). Case-insensitive, whitespace-tokenized.
/// Zero external dependencies — the lexical-only path runs when no embeddings provider is
/// registered.
/// </summary>
/// <remarks>
/// Not BM25 or TF-IDF — deliberately simple so it stays cheap, predictable, and dependency-free.
/// The semantic decorator (<see cref="SemanticToolRetriever"/>) is the rerank layer that
/// matters for ranking quality when embeddings are available.
/// </remarks>
public sealed class LexicalToolRetriever : IToolRetriever
{
    private const double NameWeight = 3.0;
    private const double TagWeight = 2.0;
    private const double DescriptionWeight = 1.0;

    /// <summary>Provides domain-ontology tags for a tool name when looking up tag matches. Default = no tags.</summary>
    private readonly Func<string, IReadOnlyList<string>>? _tagsFor;

    /// <summary>
    /// Build a lexical retriever. Pass <paramref name="tagsFor"/> when a bound
    /// <see cref="IDomainOntologyCatalog"/> can contribute extra tag terms per tool — the retriever
    /// then weights tag matches without needing to thread the catalog through every call.
    /// </summary>
    public LexicalToolRetriever(Func<string, IReadOnlyList<string>>? tagsFor = null)
    {
        _tagsFor = tagsFor;
    }

    /// <summary>
    /// Convenience constructor that pulls tags from an <see cref="IDomainOntologyCatalog"/> for
    /// every scored candidate. Equivalent to passing a closure that looks up the catalog.
    /// </summary>
    public static LexicalToolRetriever ForCatalog(IDomainOntologyCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return new LexicalToolRetriever(name => catalog.TryGetConcept(name, out var c) ? c.Tags : []);
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<ScoredTool>> RetrieveAsync(
        string query,
        IReadOnlyList<ToolDescriptor> candidates,
        int topK,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ArgumentNullException.ThrowIfNull(candidates);
        if (topK <= 0) return ValueTask.FromResult<IReadOnlyList<ScoredTool>>([]);

        var terms = Tokenize(query);
        if (terms.Length == 0) return ValueTask.FromResult<IReadOnlyList<ScoredTool>>([]);

        var scored = new List<ScoredTool>(candidates.Count);
        foreach (var c in candidates)
        {
            var score = ScoreOne(c, terms);
            if (score > 0) scored.Add(new ScoredTool(c, score));
        }

        scored.Sort(static (a, b) => b.Score.CompareTo(a.Score));
        return ValueTask.FromResult<IReadOnlyList<ScoredTool>>(
            scored.Count > topK ? scored.GetRange(0, topK) : scored);
    }

    private double ScoreOne(ToolDescriptor tool, string[] terms)
    {
        var name = tool.Name.ToLowerInvariant();
        var description = (tool.Description ?? string.Empty).ToLowerInvariant();
        var tags = _tagsFor?.Invoke(tool.Name) ?? [];
        var tagText = string.Join(' ', tags).ToLowerInvariant();

        double score = 0;
        foreach (var term in terms)
        {
            if (name.Contains(term, StringComparison.Ordinal)) score += NameWeight;
            if (tagText.Length > 0 && tagText.Contains(term, StringComparison.Ordinal)) score += TagWeight;
            if (description.Contains(term, StringComparison.Ordinal)) score += DescriptionWeight;
        }
        return score;
    }

    private static string[] Tokenize(string query)
    {
        var lower = query.ToLowerInvariant();
        var split = lower.Split(
            [' ', '\t', '\n', '\r', '.', ',', ';', ':', '!', '?', '(', ')', '[', ']', '{', '}', '"', '\''],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        // Drop trivially short stop-word-like tokens.
        var kept = new List<string>(split.Length);
        foreach (var s in split)
            if (s.Length >= 2) kept.Add(s);
        return [.. kept];
    }
}

/// <summary>
/// Decorator that reranks a base retriever's output by cosine similarity between query and
/// candidate embeddings. Opt-in: only constructed when the host registered an
/// <see cref="IEmbeddingGenerator{TInput, TEmbedding}"/>. Falls back to the base retriever's
/// ordering when an embedding call fails (degrades, does not throw).
/// </summary>
/// <remarks>
/// Plan C1-10 recall-then-rerank shape: the inner retriever (typically
/// <see cref="LexicalToolRetriever"/>) is called with an expanded K (controlled by
/// <see cref="ExpandRecallMultiplier"/>) so the reranker has a wider pool to work with;
/// the final top-K is selected from the rerank-sorted results.
/// </remarks>
public sealed class SemanticToolRetriever : IToolRetriever
{
    /// <summary>Multiplier applied to <c>topK</c> when calling the inner retriever, to give the reranker more candidates. Default 3.</summary>
    public int ExpandRecallMultiplier { get; init; } = 3;

    private readonly IToolRetriever _inner;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embedder;

    /// <summary>Build a semantic-rerank decorator over <paramref name="inner"/>.</summary>
    public SemanticToolRetriever(
        IToolRetriever inner,
        IEmbeddingGenerator<string, Embedding<float>> embedder)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(embedder);
        _inner = inner;
        _embedder = embedder;
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<ScoredTool>> RetrieveAsync(
        string query,
        IReadOnlyList<ToolDescriptor> candidates,
        int topK,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ArgumentNullException.ThrowIfNull(candidates);
        if (topK <= 0) return [];

        var recallK = Math.Max(topK, topK * ExpandRecallMultiplier);
        var recall = await _inner.RetrieveAsync(query, candidates, recallK, cancellationToken).ConfigureAwait(false);
        if (recall.Count <= 1) return recall.Count > topK ? recall.Take(topK).ToList() : recall;

        var queryEmbedding = await _embedder.GenerateAsync(query, cancellationToken: cancellationToken).ConfigureAwait(false);
        var docs = new string[recall.Count];
        for (var i = 0; i < recall.Count; i++) docs[i] = DescribeForEmbedding(recall[i].Tool);
        var docEmbeddings = await _embedder.GenerateAsync(docs, cancellationToken: cancellationToken).ConfigureAwait(false);

        var rescored = new ScoredTool[recall.Count];
        for (var i = 0; i < recall.Count; i++)
        {
            var sim = CosineSimilarity(queryEmbedding.Vector.Span, docEmbeddings[i].Vector.Span);
            rescored[i] = new ScoredTool(recall[i].Tool, sim);
        }
        Array.Sort(rescored, static (a, b) => b.Score.CompareTo(a.Score));
        return rescored.Length > topK ? rescored.AsSpan(0, topK).ToArray() : rescored;
    }

    private static string DescribeForEmbedding(ToolDescriptor t)
        => string.IsNullOrEmpty(t.Description) ? t.Name : $"{t.Name}: {t.Description}";

    private static double CosineSimilarity(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length || a.Length == 0) return 0.0;
        double dot = 0, magA = 0, magB = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            magA += a[i] * a[i];
            magB += b[i] * b[i];
        }
        var denom = Math.Sqrt(magA) * Math.Sqrt(magB);
        return denom == 0 ? 0.0 : dot / denom;
    }
}
