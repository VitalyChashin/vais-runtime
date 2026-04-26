// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Core;

/// <summary>
/// Default no-op <see cref="IAgentGraphEventBus"/>. Used when a consumer hasn't wired
/// up graph-event fan-out. Publish is a zero-cost completed <see cref="ValueTask"/>;
/// subscribe returns a disposable that does nothing. Companion to
/// <see cref="NullAgentEventBus"/>.
/// </summary>
public sealed class NullAgentGraphEventBus : IAgentGraphEventBus
{
    /// <summary>Shared singleton instance.</summary>
    public static readonly NullAgentGraphEventBus Instance = new();

    private NullAgentGraphEventBus() { }

    /// <inheritdoc />
    public ValueTask PublishAsync(AgentGraphEvent @event, CancellationToken cancellationToken = default) => default;

    /// <inheritdoc />
    public IDisposable Subscribe(Func<AgentGraphEvent, CancellationToken, ValueTask> handler) => Noop.Instance;

    private sealed class Noop : IDisposable
    {
        public static readonly Noop Instance = new();
        public void Dispose() { }
    }
}
