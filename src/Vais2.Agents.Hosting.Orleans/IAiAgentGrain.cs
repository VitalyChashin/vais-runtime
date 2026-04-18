// Copyright (c) 2026 VAIS2 Platform contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais2.Agents.Hosting.Orleans;

/// <summary>
/// Orleans-side surface for a stateful AI agent. The chat-style API mirrors
/// <see cref="IAiAgent"/> but is asynchronous end-to-end (every grain call is a
/// potential cross-silo RPC). Consumers typically interact with an agent through
/// <see cref="IAgentRuntime"/>, which hides this interface behind an
/// <see cref="IAiAgent"/> proxy.
/// </summary>
/// <remarks>
/// Grain key: the string key uniquely identifies the agent and is surfaced as
/// <see cref="Core.StatefulAgentOptions.AgentName"/> by default. Implementations are
/// expected to persist history and system prompt across deactivations via an
/// <c>IPersistentState</c>-backed store configured by the host.
/// </remarks>
public interface IAiAgentGrain : IGrainWithStringKey
{
    /// <summary>Send a user message; returns the assistant reply.</summary>
    Task<string> AskAsync(string userMessage);

    /// <summary>Snapshot of conversation history from the grain's perspective.</summary>
    Task<IReadOnlyList<ChatTurn>> GetHistoryAsync();

    /// <summary>Current system prompt (may be null).</summary>
    Task<string?> GetSystemPromptAsync();

    /// <summary>Replace the system prompt. Persists immediately.</summary>
    Task SetSystemPromptAsync(string? value);

    /// <summary>Clear history but keep the system prompt and persist.</summary>
    Task ResetAsync();

    /// <summary>Clear persisted state and deactivate on idle.</summary>
    Task DeleteAsync();
}
