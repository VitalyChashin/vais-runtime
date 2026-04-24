// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Vais.Agents;
using Vais.Agents.Core;

namespace Vais.Agents.Runtime.Host.PluginFixtureHotReload;

/// <summary>
/// V2 of the hot-reload fixture agent. Returns "Rainy!" to distinguish it
/// from V1 (which returns "Sunny!"). Loaded into a collectible
/// AssemblyLoadContext so the integration test can verify GC collection
/// after the registry swap.
/// </summary>
public sealed class WeatherAgent : IAiAgent
{
    private readonly InMemoryAgentSession _session = new(agentId: "weather-hr-v2", sessionId: "default");

    public string? SystemPrompt { get; set; }

    public IAgentSession Session => _session;

    public IReadOnlyList<ChatTurn> History => _session.History;

    public async Task<string> AskAsync(string userMessage, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userMessage);
        await _session.AppendAsync(new ChatTurn(AgentChatRole.User, userMessage), cancellationToken).ConfigureAwait(false);
        const string reply = "Rainy!";
        await _session.AppendAsync(new ChatTurn(AgentChatRole.Assistant, reply), cancellationToken).ConfigureAwait(false);
        return reply;
    }

    public void Reset() => _session.ResetAsync().AsTask().GetAwaiter().GetResult();
}
