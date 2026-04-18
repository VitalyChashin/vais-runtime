// Copyright (c) 2026 VAIS2 Platform contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais2.Agents.Core;

/// <summary>
/// Default in-process <see cref="IAgentSession"/>. Holds history in a private list;
/// mutations are synchronous but exposed through the async contract for shape
/// compatibility with remote-storage-backed implementations.
/// </summary>
/// <remarks>
/// <para>
/// Not thread-safe. Concurrent <see cref="AppendAsync"/> calls on the same instance
/// race on the list. In hosting layers that guarantee single-writer semantics
/// (Orleans grains, <c>StatefulAiAgent</c> used under a per-session lock), the
/// safety is provided by the layer above.
/// </para>
/// </remarks>
public sealed class InMemoryAgentSession : IAgentSession
{
    private readonly List<ChatTurn> _history;

    /// <summary>
    /// Create a new in-memory session.
    /// </summary>
    /// <param name="agentId">Owning agent identifier. Required, non-empty.</param>
    /// <param name="sessionId">Session identifier. When null or whitespace, a new GUID is generated.</param>
    /// <param name="initialHistory">Optional history to seed the session with, copied in order.</param>
    /// <exception cref="ArgumentException"><paramref name="agentId"/> is null, empty, or whitespace.</exception>
    public InMemoryAgentSession(string agentId, string? sessionId = null, IReadOnlyList<ChatTurn>? initialHistory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        AgentId = agentId;
        SessionId = string.IsNullOrWhiteSpace(sessionId) ? Guid.NewGuid().ToString("N") : sessionId;
        _history = initialHistory is { Count: > 0 } ? new List<ChatTurn>(initialHistory) : new List<ChatTurn>();
    }

    /// <inheritdoc />
    public string SessionId { get; }

    /// <inheritdoc />
    public string AgentId { get; }

    /// <inheritdoc />
    public IReadOnlyList<ChatTurn> History => _history;

    /// <inheritdoc />
    public ValueTask AppendAsync(ChatTurn turn, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(turn);
        _history.Add(turn);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask ResetAsync(CancellationToken cancellationToken = default)
    {
        _history.Clear();
        return ValueTask.CompletedTask;
    }
}
