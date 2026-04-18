// Copyright (c) 2026 VAIS2 Platform contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais2.Agents.Persistence.VectorData;

/// <summary>
/// An <see cref="IAgentFilter"/> that augments each turn's system prompt with chunks
/// retrieved from an <see cref="IKnowledgeRetriever"/>. Uses the last user turn in
/// <see cref="CompletionRequest.History"/> as the query; if there is no user turn, or
/// the retriever returns zero chunks, the request passes through unchanged.
/// </summary>
/// <remarks>
/// <para>
/// <b>Obsolete as of v0.4.</b> Use <see cref="KnowledgeRetrievalContextProvider"/> — same
/// semantics, cleaner shape for the architectural-review's context-provider chain.
/// This filter continues to work for one release window; removal planned for v0.5.
/// </para>
/// <para>
/// Retrieved chunks are never added to <see cref="IAiAgent.History"/> — that collection
/// tracks real conversation, not retrieved context. Only the request's
/// <see cref="CompletionRequest.SystemPrompt"/> is mutated (via <c>with</c>), and the
/// mutation is scoped to the filter's outgoing call to <c>next</c>.
/// </para>
/// <para>
/// This filter is stateless: each invocation retrieves afresh. Consumers who want
/// caching can wrap the injected <see cref="IKnowledgeRetriever"/> with their own
/// caching decorator.
/// </para>
/// </remarks>
[Obsolete("Use KnowledgeRetrievalContextProvider. KnowledgeRetrievalFilter will be removed in v0.5.", DiagnosticId = "VAIS2_0001")]
public sealed class KnowledgeRetrievalFilter : IAgentFilter
{
    private readonly IKnowledgeRetriever _retriever;
    private readonly KnowledgeRetrievalOptions _options;

    /// <summary>
    /// Create a filter that pulls context from <paramref name="retriever"/>.
    /// </summary>
    public KnowledgeRetrievalFilter(
        IKnowledgeRetriever retriever,
        KnowledgeRetrievalOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(retriever);
        _retriever = retriever;
        _options = options ?? new KnowledgeRetrievalOptions();
    }

    /// <inheritdoc />
    public async Task<CompletionResponse> InvokeAsync(
        CompletionRequest request,
        Func<CompletionRequest, CancellationToken, Task<CompletionResponse>> next,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(next);

        var query = ExtractQuery(request);
        if (query is null)
        {
            return await next(request, cancellationToken).ConfigureAwait(false);
        }

        var chunks = await _retriever
            .RetrieveAsync(query, _options.TopK, cancellationToken)
            .ConfigureAwait(false);

        if (chunks.Count == 0)
        {
            return await next(request, cancellationToken).ConfigureAwait(false);
        }

        var augmented = request with { SystemPrompt = BuildAugmentedPrompt(request.SystemPrompt, chunks) };
        return await next(augmented, cancellationToken).ConfigureAwait(false);
    }

    private static string? ExtractQuery(CompletionRequest request)
    {
        for (var i = request.History.Count - 1; i >= 0; i--)
        {
            var turn = request.History[i];
            if (turn.Role == AgentChatRole.User && !string.IsNullOrWhiteSpace(turn.Text))
            {
                return turn.Text;
            }
        }
        return null;
    }

    private string BuildAugmentedPrompt(string? original, IReadOnlyList<KnowledgeChunk> chunks)
    {
        var joined = string.Join(_options.ChunkSeparator, chunks.Select(c => c.Text));
        var contextBlock = _options.Template.Replace("{chunks}", joined, StringComparison.Ordinal);

        if (string.IsNullOrEmpty(original))
        {
            return contextBlock;
        }

        return $"{original}\n\n{contextBlock}";
    }
}
