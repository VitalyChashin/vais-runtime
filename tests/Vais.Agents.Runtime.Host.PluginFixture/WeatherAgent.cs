// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Vais.Agents;
using Vais.Agents.Core;

namespace Vais.Agents.Runtime.Host.PluginFixture;

/// <summary>
/// Trivial IAiAgent for the v0.18 PluginLoadingIntegrationTests. Returns
/// "Sunny!" on every AskAsync call. Resolved by the loader's default
/// handler factory (convention path, no explicit IAgentHandlerFactory).
/// </summary>
public sealed class WeatherAgent : IAiAgent
{
    private readonly InMemoryAgentSession _session = new(agentId: "weather-fixture", sessionId: "default");

    public string? SystemPrompt { get; set; }

    public IAgentSession Session => _session;

    public IReadOnlyList<ChatTurn> History => _session.History;

    public async Task<string> AskAsync(string userMessage, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userMessage);
        await _session.AppendAsync(new ChatTurn(AgentChatRole.User, userMessage), cancellationToken).ConfigureAwait(false);
        const string reply = "Sunny!";
        await _session.AppendAsync(new ChatTurn(AgentChatRole.Assistant, reply), cancellationToken).ConfigureAwait(false);
        return reply;
    }

    public void Reset() => _session.ResetAsync().AsTask().GetAwaiter().GetResult();
}
