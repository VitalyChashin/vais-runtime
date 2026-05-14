// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Vais.Agents.Core;

namespace Vais.Agents.Hosting.InMemory;

/// <summary>
/// Single-process <see cref="IAgentRuntime"/>. Agents are cached by id in a
/// <see cref="ConcurrentDictionary{TKey, TValue}"/>; each one wraps the shared
/// <see cref="ICompletionProvider"/> in a fresh <see cref="StatefulAiAgent"/>.
/// </summary>
/// <remarks>
/// <para>
/// Use this for dev, tests, and samples. It provides no durability, no clustering,
/// no memory bounds — an agent cache that grows forever in a long-lived process.
/// </para>
/// <para>
/// Agents created here share the same provider instance. Concurrent calls to
/// <see cref="IAiAgent.AskAsync"/> on the <em>same</em> agent are not safe (see
/// <see cref="StatefulAiAgent"/>); calls across distinct agent ids are safe.
/// </para>
/// </remarks>
public sealed class InMemoryAgentRuntime : IAgentRuntime
{
    private readonly ConcurrentDictionary<string, IAiAgent> _agents = new(StringComparer.Ordinal);
    private readonly ICompletionProvider _provider;
    private readonly Func<string, StatefulAgentOptions> _optionsFactory;
    private readonly ILoggerFactory _loggerFactory;

    /// <summary>
    /// Create a runtime that creates agents on demand from a shared provider.
    /// </summary>
    /// <param name="provider">The provider every created agent will use.</param>
    /// <param name="optionsFactory">
    /// Given an agent id, returns the <see cref="StatefulAgentOptions"/> for that
    /// agent. Called exactly once per id. Defaults to a plain-options factory.
    /// </param>
    /// <param name="loggerFactory">Optional logger factory for created agents.</param>
    public InMemoryAgentRuntime(
        ICompletionProvider provider,
        Func<string, StatefulAgentOptions>? optionsFactory = null,
        ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _provider = provider;
        _optionsFactory = optionsFactory ?? (id => new StatefulAgentOptions { AgentName = id });
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
    }

    /// <inheritdoc />
    public IAiAgent GetOrCreate(string agentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        return _agents.GetOrAdd(agentId, CreateAgent);
    }

    /// <inheritdoc />
    public bool TryGet(string agentId, out IAiAgent? agent)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        if (_agents.TryGetValue(agentId, out var found))
        {
            agent = found;
            return true;
        }

        agent = null;
        return false;
    }

    /// <inheritdoc />
    public IAiAgent GetOrCreateForSession(string agentId, string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        return _agents.GetOrAdd($"{agentId}/{sessionId}", _ => CreateAgent(agentId));
    }

    /// <inheritdoc />
    public bool Remove(string agentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        return _agents.TryRemove(agentId, out _);
    }

    /// <inheritdoc />
    public bool RemoveSession(string agentId, string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        return _agents.TryRemove($"{agentId}/{sessionId}", out _);
    }

    private IAiAgent CreateAgent(string agentId)
    {
        var options = _optionsFactory(agentId);
        return new StatefulAiAgent(
            _provider,
            options,
            _loggerFactory.CreateLogger<StatefulAiAgent>());
    }
}
