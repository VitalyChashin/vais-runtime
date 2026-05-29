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

    private readonly StatefulAiAgent _inner;

    public ResearchPlannerAgent(
        ICompletionProvider provider,
        IEnumerable<LlmGatewayMiddleware>? middleware = null,
        IEnumerable<ISectionTelemetrySink>? sectionSinks = null)
    {
        _inner = new StatefulAiAgent(provider, new StatefulAgentOptions
        {
            AgentName = "research-planner",
            SystemPrompt = DefaultSystemPrompt,
            GatewayMiddleware = middleware?.ToArray() ?? [],
            SectionTelemetrySinks = sectionSinks?.ToArray() ?? [],
        });
    }

    /// <inheritdoc />
    public string? SystemPrompt
    {
        get => _inner.SystemPrompt;
        set => _inner.SystemPrompt = value;
    }

    /// <inheritdoc />
    public IAgentSession Session => _inner.Session;

    /// <inheritdoc />
    public IReadOnlyList<ChatTurn> History => _inner.History;

    /// <inheritdoc />
    public Task<string> AskAsync(string userMessage, CancellationToken cancellationToken = default)
        => _inner.AskAsync(userMessage, cancellationToken);

    /// <inheritdoc />
    public void Reset() => _inner.Reset();
}
