// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Collections.Concurrent;

namespace Vais.Agents.Hosting.Orleans;

/// <summary>
/// <see cref="IAgentRuntime"/> backed by Orleans virtual actors. Each agent id maps
/// one-to-one to an <see cref="IAiAgentGrain"/> of the same key; the proxy returned
/// from <see cref="GetOrCreate"/> forwards to that grain.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="TryGet"/> reports existence of a client-side <em>proxy</em> cached by a
/// prior call to <see cref="GetOrCreate"/>; it does not probe the silo. Orleans grains
/// are virtual — they exist on demand, so asking "does this grain exist?" without
/// observing its state is not a meaningful question. Consumers that need authoritative
/// presence should inspect grain state directly.
/// </para>
/// <para>
/// <see cref="Remove"/> both evicts the client-side proxy and fires-and-forgets a grain
/// <see cref="IAiAgentGrain.DeleteAsync"/>. Durable state is cleared asynchronously; a
/// subsequent <see cref="GetOrCreate"/> with the same id re-creates the grain in a
/// clean state.
/// </para>
/// </remarks>
public sealed class OrleansAgentRuntime : IAgentRuntime
{
    private readonly IGrainFactory _grainFactory;
    private readonly ConcurrentDictionary<string, OrleansAiAgentProxy> _proxies = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, OrleansAiAgentProxy> _sessionProxies = new(StringComparer.Ordinal);

    /// <summary>
    /// Create a runtime over the given grain factory.
    /// </summary>
    public OrleansAgentRuntime(IGrainFactory grainFactory)
    {
        ArgumentNullException.ThrowIfNull(grainFactory);
        _grainFactory = grainFactory;
    }

    /// <inheritdoc />
    public IAiAgent GetOrCreate(string agentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        return _proxies.GetOrAdd(agentId, id => new OrleansAiAgentProxy(_grainFactory.GetGrain<IAiAgentGrain>(id), id));
    }

    /// <inheritdoc />
    public bool TryGet(string agentId, out IAiAgent? agent)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        if (_proxies.TryGetValue(agentId, out var proxy))
        {
            agent = proxy;
            return true;
        }
        agent = null;
        return false;
    }

    /// <inheritdoc />
    public IAiAgent GetOrCreateForSession(string agentId, string sessionId)
    {
        var key = OrleansSessionGrainKey.Build(agentId, sessionId);
        return _sessionProxies.GetOrAdd(key, k => new OrleansAiAgentProxy(
            _grainFactory.GetGrain<IAiAgentGrain>(k), agentId));
    }

    /// <inheritdoc />
    public bool Remove(string agentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        var removed = _proxies.TryRemove(agentId, out _);
        _ = _grainFactory.GetGrain<IAiAgentGrain>(agentId).DeleteAsync();
        return removed;
    }

    /// <inheritdoc />
    public bool RemoveSession(string agentId, string sessionId)
    {
        var key = OrleansSessionGrainKey.Build(agentId, sessionId);
        var removed = _sessionProxies.TryRemove(key, out _);
        _ = _grainFactory.GetGrain<IAiAgentGrain>(key).DeleteAsync();
        return removed;
    }

    /// <summary>
    /// Get an <see cref="IAgentSession"/> bound to a specific session of the given agent.
    /// Multiple sessions of the same agent run in distinct grain instances and therefore
    /// serialise only per-session, not per-agent. Compose with
    /// <see cref="Core.StatefulAiAgent"/> via
    /// <see cref="Core.StatefulAgentOptions.Session"/> to run the turn-loop locally while
    /// history is durably owned by the silo.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Either id is null, empty, whitespace, or contains the reserved <c>/</c> separator.
    /// </exception>
    public IAgentSession GetSession(string agentId, string sessionId)
    {
        var key = OrleansSessionGrainKey.Build(agentId, sessionId);
        var grain = _grainFactory.GetGrain<IAgentSessionGrain>(key);
        return new OrleansAgentSession(grain, agentId, sessionId);
    }

    /// <summary>
    /// Get the per-agent config grain (system prompt + shared config) for
    /// <paramref name="agentId"/>. Typically consumed by host-side code that wires
    /// session grains and wants to share config across them.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="agentId"/> is null, empty, or whitespace.</exception>
    public IAgentConfigGrain GetAgentConfig(string agentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        return _grainFactory.GetGrain<IAgentConfigGrain>(agentId);
    }

    /// <summary>
    /// Get an <see cref="IAgentJournal"/> backed by Orleans grains — each <c>RunId</c>
    /// routes to an <see cref="IAgentRunJournalGrain"/> of the same key. Compose with
    /// <see cref="Core.StatefulAiAgent"/> via <see cref="Core.StatefulAgentOptions.Journal"/>
    /// to run the turn-loop locally while the durable-execution journal lives on the silo.
    /// </summary>
    public IAgentJournal GetJournal() => new OrleansAgentJournal(_grainFactory);
}
