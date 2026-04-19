// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents;

/// <summary>
/// Minimal stateful agent contract. Holds conversation history across turns and
/// delegates each turn to an <see cref="ICompletionProvider"/>.
/// </summary>
/// <remarks>
/// This is the surface most consumers depend on. It is deliberately small — the
/// library adds richer operations (streaming, tools, multi-agent, events) as
/// separate interfaces rather than expanding this one.
/// </remarks>
public interface IAiAgent
{
    /// <summary>
    /// Optional system instruction prepended to every turn. May be set at construction
    /// or mutated between turns; implementations must observe the current value at
    /// invocation time.
    /// </summary>
    string? SystemPrompt { get; set; }

    /// <summary>
    /// The conversation container this agent is bound to. History and per-conversation
    /// state live here; <see cref="History"/> is a shim over <see cref="IAgentSession.History"/>.
    /// </summary>
    IAgentSession Session { get; }

    /// <summary>Read-only view of the conversation history known to this agent.</summary>
    /// <remarks>Equivalent to <see cref="Session"/>.<see cref="IAgentSession.History"/>.</remarks>
    IReadOnlyList<ChatTurn> History { get; }

    /// <summary>
    /// Send a user message and await the assistant reply. Both messages are appended
    /// to <see cref="History"/>.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="userMessage"/> is null or whitespace.</exception>
    Task<string> AskAsync(string userMessage, CancellationToken cancellationToken = default);

    /// <summary>
    /// Clear all history. The system prompt (if any) is retained.
    /// </summary>
    void Reset();
}
