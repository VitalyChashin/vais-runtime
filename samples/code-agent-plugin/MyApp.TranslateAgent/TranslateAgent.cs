// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Vais.Agents;

namespace MyApp.TranslateAgent;

/// <summary>
/// Code-authored plugin agent that translates text to a target language using the
/// OpenAI chat completions API. Demonstrates injecting IHttpClientFactory from the
/// runtime host DI container and reading agent-level config from the manifest's
/// spec.properties bag.
/// </summary>
/// <remarks>
/// The target language defaults to Spanish. Override via the manifest's
/// spec.properties.targetLanguage field:
/// <code>
/// spec:
///   properties:
///     targetLanguage: French
/// </code>
/// </remarks>
public sealed class TranslateAgent : IAiAgent
{
    private readonly InMemoryAgentSession _session = new(agentId: "translate", sessionId: "default");
    private readonly IHttpClientFactory _httpFactory;

    public TranslateAgent(IHttpClientFactory httpFactory)
    {
        _httpFactory = httpFactory;
    }

    public string? SystemPrompt { get; set; }

    public IAgentSession Session => _session;

    public IReadOnlyList<ChatTurn> History => _session.History;

    public async Task<string> AskAsync(string userMessage, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userMessage);

        var targetLanguage = ExtractTargetLanguage(SystemPrompt) ?? "Spanish";
        var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            var fallback = $"[TranslateAgent] No OPENAI_API_KEY set — would translate to {targetLanguage}: \"{userMessage}\"";
            await RecordAsync(userMessage, fallback, cancellationToken);
            return fallback;
        }

        var client = _httpFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", apiKey);

        var body = new
        {
            model = "gpt-4o-mini",
            messages = new[]
            {
                new { role = "system", content = $"Translate the user's message to {targetLanguage}. Return only the translated text, nothing else." },
                new { role = "user",   content = userMessage }
            }
        };

        var response = await client.PostAsJsonAsync(
            "https://api.openai.com/v1/chat/completions", body, cancellationToken);

        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<CompletionResponse>(
            JsonSerializerOptions.Default, cancellationToken) ?? throw new InvalidOperationException("Null response");

        var reply = json.Choices[0].Message.Content;
        await RecordAsync(userMessage, reply, cancellationToken);
        return reply;
    }

    public void Reset() => _session.ResetAsync().AsTask().GetAwaiter().GetResult();

    private async ValueTask RecordAsync(string user, string assistant, CancellationToken ct)
    {
        await _session.AppendAsync(new ChatTurn(AgentChatRole.User, user), ct);
        await _session.AppendAsync(new ChatTurn(AgentChatRole.Assistant, assistant), ct);
    }

    private static string? ExtractTargetLanguage(string? systemPrompt)
    {
        if (systemPrompt is null) return null;
        const string marker = "targetLanguage:";
        var idx = systemPrompt.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        return idx < 0 ? null : systemPrompt[(idx + marker.Length)..].Trim();
    }

    private sealed record CompletionResponse(
        [property: JsonPropertyName("choices")] List<Choice> Choices);

    private sealed record Choice(
        [property: JsonPropertyName("message")] MessageContent Message);

    private sealed record MessageContent(
        [property: JsonPropertyName("content")] string Content);
}
