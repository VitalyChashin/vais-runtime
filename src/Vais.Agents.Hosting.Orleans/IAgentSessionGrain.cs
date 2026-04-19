// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Hosting.Orleans;

/// <summary>
/// Orleans-side surface for a per-session conversation container. Each grain
/// instance holds the history (and future per-session state) for a single
/// <c>(agentId, sessionId)</c> pair.
/// </summary>
/// <remarks>
/// <para>
/// <b>Grain key.</b> <see cref="IGrainWithStringKey"/>. The string key must be of the
/// form <c>"{agentId}/{sessionId}"</c>; use <see cref="OrleansSessionGrainKey"/> to
/// build or parse it. Neither component may contain a <c>/</c>.
/// </para>
/// <para>
/// <b>Single-writer guarantee.</b> Orleans serialises calls per grain, which means
/// per <c>(agentId, sessionId)</c> here. Two concurrent calls to the same session
/// queue; two concurrent calls to different sessions of the same agent (same
/// <c>agentId</c>, different <c>sessionId</c>) run in parallel — they live in
/// distinct grain instances.
/// </para>
/// </remarks>
public interface IAgentSessionGrain : IGrainWithStringKey
{
    /// <summary>Append a single turn to the session's durable history.</summary>
    Task AppendAsync(ChatTurn turn);

    /// <summary>Snapshot of the session's history, in order.</summary>
    Task<IReadOnlyList<ChatTurn>> GetHistoryAsync();

    /// <summary>Clear the session's history. Identity is preserved.</summary>
    Task ResetAsync();

    /// <summary>Clear persisted state and deactivate on idle.</summary>
    Task DeleteAsync();
}
