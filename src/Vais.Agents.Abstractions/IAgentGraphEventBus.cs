// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents;

/// <summary>
/// Fan-out for <see cref="AgentGraphEvent"/> instances produced by an <see cref="IAgentGraph{TState}"/>
/// during graph execution. Parallel to <see cref="IAgentEventBus"/> (per-turn taxonomy) but
/// graph-scoped — carries run-id + super-step so consumers can correlate against the
/// checkpoint timeline without subscribing to the enumerable directly.
/// </summary>
/// <remarks>
/// <para>
/// Publish is best-effort — bus implementations should not throw on bad subscribers.
/// A published event must not break the graph's main flow; failures are logged and
/// swallowed on the implementation side, matching the contract of <see cref="IAgentEventBus"/>.
/// </para>
/// <para>
/// Thread-safety is implementation-defined; the in-memory bus and any custom implementation
/// must support concurrent publish and subscribe from multiple threads.
/// </para>
/// </remarks>
public interface IAgentGraphEventBus
{
    /// <summary>
    /// Publish an event to every current subscriber. Returns when all subscribers
    /// have observed the event (or their handlers have thrown, in which case the
    /// bus suppresses the exception).
    /// </summary>
    ValueTask PublishAsync(AgentGraphEvent @event, CancellationToken cancellationToken = default);

    /// <summary>
    /// Register a handler for future events. Dispose the returned handle to
    /// unsubscribe. Handlers are invoked in registration order.
    /// </summary>
    IDisposable Subscribe(Func<AgentGraphEvent, CancellationToken, ValueTask> handler);
}
