// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Vais.Agents;
using Vais.Agents.Core;

namespace MyApp.ResearchPlannerAgent;

/// <summary>
/// Plugin agent that decomposes a user research query into 3–5 focused
/// sub-questions via the Vais.Agents LLM gateway. Output is plain text, one
/// question per line — suitable as input to the LangGraph researcher node in
/// the research pipeline.
/// </summary>
public sealed class ResearchPlannerAgent : IAiAgent
{
    private static readonly string DefaultSystemPrompt = """
        You are a research planning expert. Break the user's topic into 3 to 5
        specific, focused sub-questions that together fully cover the subject.
        Each question must be independently researchable via web search.
        Output exactly one question per line. No numbering, no bullet points,
        no preamble, no trailing punctuation beyond a question mark.
        """;

    private readonly InMemoryAgentSession _session;
    private readonly ICompletionProvider _provider;
    private readonly LlmGatewayMiddleware[] _middleware;

    public ResearchPlannerAgent(
        ICompletionProvider provider,
        IEnumerable<LlmGatewayMiddleware>? middleware = null)
    {
        _session = new InMemoryAgentSession(agentId: "research-planner", sessionId: Guid.NewGuid().ToString("N"));
        _provider = provider;
        _middleware = middleware?.ToArray() ?? [];
    }

    /// <inheritdoc />
    public string? SystemPrompt { get; set; }

    /// <inheritdoc />
    public IAgentSession Session => _session;

    /// <inheritdoc />
    public IReadOnlyList<ChatTurn> History => _session.History;

    /// <inheritdoc />
    public async Task<string> AskAsync(string userMessage, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userMessage);
        return await AskViaGatewayAsync(userMessage, cancellationToken);
    }

    private async Task<string> AskViaGatewayAsync(string userMessage, CancellationToken cancellationToken)
    {
        var history = new List<ChatTurn>(_session.History)
        {
            new(AgentChatRole.User, userMessage),
        };
        var request = new CompletionRequest(
            History: history,
            SystemPrompt: SystemPrompt ?? DefaultSystemPrompt);

        var response = await LlmGatewayPipeline.InvokeAsync(request, _provider, _middleware, cancellationToken);
        var reply = response.Text;
        await RecordAsync(userMessage, reply, cancellationToken);
        return reply;
    }

    /// <inheritdoc />
    public void Reset() => _session.ResetAsync().AsTask().GetAwaiter().GetResult();

    private async ValueTask RecordAsync(string user, string assistant, CancellationToken ct)
    {
        await _session.AppendAsync(new ChatTurn(AgentChatRole.User, user), ct);
        await _session.AppendAsync(new ChatTurn(AgentChatRole.Assistant, assistant), ct);
    }
}
