// Copyright (c) 2026 VAIS2 Platform contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais2.Agents;

/// <summary>
/// Fan-out for <see cref="AgentEvent"/> instances produced by an agent. Replaces
/// VAIS2's grain-level stream subscriptions (<c>GetStreamProvider("StreamProvider")</c>)
/// with a stack-neutral contract that works against in-process subscribers,
/// Orleans streams backed by Redis, or any custom transport.
/// </summary>
/// <remarks>
/// <para>
/// Publish is best-effort — bus implementations should not throw on bad subscribers.
/// Core treats publishing like <see cref="IUsageSink"/>: a published event must not
/// break the agent's main flow, so failures are logged and swallowed on the Core side.
/// </para>
/// <para>
/// The bus's thread-safety is implementation-defined; the in-memory bus and the
/// Orleans-streams-backed bus both support concurrent publish and subscribe from
/// multiple threads.
/// </para>
/// </remarks>
public interface IAgentEventBus
{
    /// <summary>
    /// Publish an event to every current subscriber. Returns when all subscribers
    /// have observed the event (or their handlers have thrown, in which case the
    /// bus suppresses the exception).
    /// </summary>
    ValueTask PublishAsync(AgentEvent @event, CancellationToken cancellationToken = default);

    /// <summary>
    /// Register a handler for future events. Dispose the returned handle to
    /// unsubscribe. Handlers are invoked in registration order.
    /// </summary>
    IDisposable Subscribe(Func<AgentEvent, CancellationToken, ValueTask> handler);
}
