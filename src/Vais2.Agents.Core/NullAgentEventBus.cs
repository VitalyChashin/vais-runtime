// Copyright (c) 2026 VAIS2 Platform contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais2.Agents.Core;

/// <summary>
/// Default no-op <see cref="IAgentEventBus"/>. Used when a consumer hasn't wired
/// up event fan-out. Publish is a zero-cost completed <see cref="ValueTask"/>;
/// subscribe returns a disposable that does nothing. Companion to
/// <see cref="NullUsageSink"/>.
/// </summary>
public sealed class NullAgentEventBus : IAgentEventBus
{
    /// <summary>Shared singleton instance.</summary>
    public static readonly NullAgentEventBus Instance = new();

    private NullAgentEventBus() { }

    /// <inheritdoc />
    public ValueTask PublishAsync(AgentEvent @event, CancellationToken cancellationToken = default) => default;

    /// <inheritdoc />
    public IDisposable Subscribe(Func<AgentEvent, CancellationToken, ValueTask> handler) => Noop.Instance;

    private sealed class Noop : IDisposable
    {
        public static readonly Noop Instance = new();
        public void Dispose() { }
    }
}
