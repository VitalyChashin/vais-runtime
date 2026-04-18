// Copyright (c) 2026 VAIS2 Platform contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Collections.Concurrent;

namespace Vais2.Agents.Hosting.Orleans;

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
    public bool Remove(string agentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        var removed = _proxies.TryRemove(agentId, out _);
        _ = _grainFactory.GetGrain<IAiAgentGrain>(agentId).DeleteAsync();
        return removed;
    }
}
