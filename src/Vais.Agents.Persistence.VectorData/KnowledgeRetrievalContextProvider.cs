// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Persistence.VectorData;

/// <summary>
/// An <see cref="IContextProvider"/> that augments each turn's system prompt with
/// chunks retrieved from an <see cref="IKnowledgeRetriever"/>. Uses the last user
/// turn in <see cref="ContextInvocationContext.Candidate"/>'s history as the query;
/// if no user turn exists, or the retriever returns zero chunks, contributes nothing.
/// </summary>
/// <remarks>
/// <para>
/// v0.4 replacement for the pre-0.4 <see cref="KnowledgeRetrievalFilter"/>. Same
/// retrieval and template semantics; the only difference is the shape — this returns
/// a <see cref="ContextContribution"/> with a <see cref="ContextContribution.SystemPromptAddendum"/>
/// that the host merges into the candidate system prompt with a <c>"\n\n"</c>
/// separator, instead of mutating the request in-line via an <see cref="IAgentFilter"/>.
/// </para>
/// <para>
/// Retrieved chunks are never added to the session's history — that collection tracks
/// real conversation, not retrieved context.
/// </para>
/// <para>
/// Stateless: each invocation retrieves afresh. Consumers who want caching can wrap
/// the injected <see cref="IKnowledgeRetriever"/> with their own caching decorator.
/// </para>
/// </remarks>
public sealed class KnowledgeRetrievalContextProvider : IContextProvider
{
    private readonly IKnowledgeRetriever _retriever;
    private readonly KnowledgeRetrievalOptions _options;

    /// <summary>Create a provider that pulls context from <paramref name="retriever"/>.</summary>
    public KnowledgeRetrievalContextProvider(
        IKnowledgeRetriever retriever,
        KnowledgeRetrievalOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(retriever);
        _retriever = retriever;
        _options = options ?? new KnowledgeRetrievalOptions();
    }

    /// <inheritdoc />
    public async ValueTask<ContextContribution> InvokeAsync(
        ContextInvocationContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var query = ExtractQuery(context.Candidate);
        if (query is null)
        {
            return ContextContribution.Empty;
        }

        var chunks = await _retriever
            .RetrieveAsync(query, _options.TopK, cancellationToken)
            .ConfigureAwait(false);

        if (chunks.Count == 0)
        {
            return ContextContribution.Empty;
        }

        var joined = string.Join(_options.ChunkSeparator, chunks.Select(c => c.Text));
        var contextBlock = _options.Template.Replace("{chunks}", joined, StringComparison.Ordinal);

        // Emit a section-shaped contribution. Priority 5 (mid) — retrieved context is droppable
        // under budget pressure ahead of persona (priority 0) but before opt-in extras (7+).
        var section = new Section(
            _options.SectionId,
            SectionKind.SystemSegment,
            new TextPayload(contextBlock),
            ProducerId: nameof(KnowledgeRetrievalContextProvider),
            Budget: new SectionBudget(Priority: 5));

        return new ContextContribution(new[] { section });
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
}
