// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents;

/// <summary>
/// Hosts and addresses agents by stable identifier. A host is what turns a
/// one-shot <see cref="ICompletionProvider"/> + <see cref="IAiAgent"/> class into
/// something persistent that survives calls and can be load-balanced across nodes.
/// </summary>
/// <remarks>
/// <para>
/// The minimal contract: given an <c>agentId</c>, return a cached
/// (or freshly activated) <see cref="IAiAgent"/>. What "cached" means is up to
/// the host — the in-process host uses a <see cref="System.Collections.Concurrent.ConcurrentDictionary{TKey, TValue}"/>,
/// the Orleans host (later milestone) uses virtual actors.
/// </para>
/// <para>
/// Consumers should depend on this interface rather than on <see cref="IAiAgent"/>
/// directly when they want the library's hosting semantics.
/// </para>
/// </remarks>
public interface IAgentRuntime
{
    /// <summary>
    /// Obtain the agent with the given id. Creates it on first access; subsequent
    /// calls with the same id return the same instance (host-defined lifetime).
    /// </summary>
    /// <param name="agentId">Stable string identifier. Case-sensitive.</param>
    IAiAgent GetOrCreate(string agentId);

    /// <summary>
    /// Try to fetch an existing agent without creating one.
    /// </summary>
    bool TryGet(string agentId, out IAiAgent? agent);

    /// <summary>
    /// Evict a cached agent. Returns <c>true</c> if an agent existed and was removed.
    /// </summary>
    bool Remove(string agentId);
}
