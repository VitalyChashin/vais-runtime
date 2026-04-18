// Copyright (c) 2026 VAIS2 Platform contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais2.Agents;

/// <summary>
/// A named, keyed conversation container. The canonical boundary for per-conversation
/// state — history, and (future) checkpoints.
/// </summary>
/// <remarks>
/// <para>
/// An agent (identified by <see cref="AgentId"/>) may own many concurrent sessions.
/// Each session is independently addressable and, at the hosting layer (Orleans),
/// carries its own single-writer guarantee: two turns against the same session
/// serialise; two turns against different sessions of the same agent run in parallel.
/// </para>
/// <para>
/// The <see cref="History"/> property is synchronous by design. Implementations are
/// expected to materialise the history locally so callers can read it without
/// awaiting. Implementations backed by remote storage should load on activation and
/// cache in-memory.
/// </para>
/// <para>
/// Mutation operations (<see cref="AppendAsync"/>, <see cref="ResetAsync"/>) are
/// asynchronous to accommodate implementations that persist to remote storage.
/// </para>
/// </remarks>
public interface IAgentSession
{
    /// <summary>Stable identifier for this session. Never null, never empty.</summary>
    string SessionId { get; }

    /// <summary>Identifier of the agent this session belongs to. Never null, never empty.</summary>
    string AgentId { get; }

    /// <summary>Snapshot of the conversation turns recorded on this session, in order.</summary>
    IReadOnlyList<ChatTurn> History { get; }

    /// <summary>
    /// Append a turn to the session. Completes only after the turn is durably recorded
    /// for persistent implementations.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="turn"/> is null.</exception>
    ValueTask AppendAsync(ChatTurn turn, CancellationToken cancellationToken = default);

    /// <summary>
    /// Clear the session's history. Identity (<see cref="SessionId"/> / <see cref="AgentId"/>)
    /// is preserved.
    /// </summary>
    ValueTask ResetAsync(CancellationToken cancellationToken = default);
}
