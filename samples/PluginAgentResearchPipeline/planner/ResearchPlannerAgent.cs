// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Vais.Agents;
using Vais.Agents.Core;
#pragma warning disable CA2000 // HttpClient intentionally not disposed per-agent (long-lived)

namespace MyApp.ResearchPlannerAgent;

/// <summary>
/// Plugin agent that decomposes a user research query into 3–5 focused
/// sub-questions via OpenAI. Output is plain text, one question per line —
/// suitable as input to the LangGraph researcher node in the research pipeline.
/// </summary>
/// <remarks>
/// Reads <c>OPENAI_API_KEY</c> from the environment (set via the runtime compose
/// env or a <c>secret://env/OPENAI_API_KEY</c> secret ref in the agent manifest).
/// Falls back to a stub response if the key is absent so the plugin can be
/// exercised without a live OpenAI account.
/// </remarks>
public sealed class ResearchPlannerAgent : IAiAgent
{
    private const string Model = "gpt-4o-mini";
    private const string OpenAiEndpoint = "https://api.openai.com/v1/chat/completions";

    private static readonly string DefaultSystemPrompt = """
        You are a research planning expert. Break the user's topic into 3 to 5
        specific, focused sub-questions that together fully cover the subject.
        Each question must be independently researchable via web search.
        Output exactly one question per line. No numbering, no bullet points,
        no preamble, no trailing punctuation beyond a question mark.
        """;

    private readonly InMemoryAgentSession _session;
    private readonly HttpClient _http = new();

    public ResearchPlannerAgent()
    {
        _session = new InMemoryAgentSession(agentId: "research-planner", sessionId: Guid.NewGuid().ToString("N"));
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

        var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            var stub = $"What are the key concepts in: {userMessage}?\n" +
                       $"What are the main challenges related to: {userMessage}?\n" +
                       $"What are recent developments regarding: {userMessage}?";
            await RecordAsync(userMessage, stub, cancellationToken);
            return stub;
        }

        var client = _http;
        client.DefaultRequestHeaders.Authorization = new("Bearer", apiKey);

        var body = new
        {
            model = Model,
            messages = new[]
            {
                new { role = "system", content = SystemPrompt ?? DefaultSystemPrompt },
                new { role = "user",   content = userMessage }
            }
        };

        var response = await client.PostAsJsonAsync(OpenAiEndpoint, body, cancellationToken);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadFromJsonAsync<CompletionResponse>(
            JsonSerializerOptions.Default, cancellationToken)
            ?? throw new InvalidOperationException("Null response from OpenAI");

        var reply = json.Choices[0].Message.Content;
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

    private sealed record CompletionResponse(
        [property: JsonPropertyName("choices")] List<Choice> Choices);

    private sealed record Choice(
        [property: JsonPropertyName("message")] MessageContent Message);

    private sealed record MessageContent(
        [property: JsonPropertyName("content")] string Content);
}
