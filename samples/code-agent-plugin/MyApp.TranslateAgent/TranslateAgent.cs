// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Vais.Agents;
using Vais.Agents.Core;

namespace MyApp.TranslateAgent;

/// <summary>
/// Code-authored plugin agent that translates text to a target language via the
/// Vais.Agents LLM gateway. Demonstrates injecting ICompletionProvider from the
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
    private readonly ICompletionProvider _provider;

    public TranslateAgent(ICompletionProvider provider)
    {
        _provider = provider;
    }

    public string? SystemPrompt { get; set; }

    public IAgentSession Session => _session;

    public IReadOnlyList<ChatTurn> History => _session.History;

    public async Task<string> AskAsync(string userMessage, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userMessage);

        var targetLanguage = ExtractTargetLanguage(SystemPrompt) ?? "Spanish";
        var history = new[]
        {
            new ChatTurn(AgentChatRole.System,
                $"Translate the user's message to {targetLanguage}. Return only the translated text, nothing else."),
            new ChatTurn(AgentChatRole.User, userMessage),
        };
        var request = new CompletionRequest(history);
        var response = await LlmGatewayPipeline.InvokeAsync(request, _provider, [], cancellationToken);
        var reply = response.Text;
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
}
