// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Vais.Agents;

namespace MyApp.WeatherAgent;

/// <summary>
/// Trivial IAiAgent implementation for the v0.18 Pillar C plugin sample. Returns a
/// hardcoded "Sunny!" reply — no outbound HTTP, so the sample is hermetic and can
/// run in any environment without API keys or network access.
/// </summary>
/// <remarks>
/// Real partners replace <see cref="AskAsync"/> with their own logic and inject
/// <c>IHttpClientFactory</c>, <c>ICompletionProvider</c>, <c>ISecretResolver</c>, or
/// any service registered in the host DI container via a constructor parameter.
/// The loader's default handler factory uses <c>ActivatorUtilities.CreateInstance</c>
/// so constructor-injected services resolve from the runtime's full
/// <see cref="IServiceProvider"/>.
/// </remarks>
public sealed class WeatherAgent : IAiAgent
{
    private readonly InMemoryAgentSession _session = new(agentId: "weather", sessionId: "default");

    public string? SystemPrompt { get; set; }

    public IAgentSession Session => _session;

    public IReadOnlyList<ChatTurn> History => _session.History;

    public async Task<string> AskAsync(string userMessage, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userMessage);
        await _session.AppendAsync(new ChatTurn(AgentChatRole.User, userMessage), cancellationToken);
        var reply = "Sunny!";
        await _session.AppendAsync(new ChatTurn(AgentChatRole.Assistant, reply), cancellationToken);
        return reply;
    }

    public void Reset() => _session.ResetAsync().AsTask().GetAwaiter().GetResult();
}
