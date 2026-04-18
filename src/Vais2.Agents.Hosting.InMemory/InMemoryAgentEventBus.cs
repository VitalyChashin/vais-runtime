// Copyright (c) 2026 VAIS2 Platform contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Vais2.Agents.Hosting.InMemory;

/// <summary>
/// In-process <see cref="IAgentEventBus"/>. Subscribers are invoked in registration
/// order on the publishing thread; a subscriber whose handler throws has its
/// exception logged and swallowed so it cannot disrupt the remaining fan-out.
/// </summary>
/// <remarks>
/// <para>
/// Not durable, not cross-process: for that, use the Orleans-streams-backed bus
/// shipped in <c>Vais2.Agents.Persistence.Redis</c>. This bus suits samples, tests,
/// and single-process hosts that just want in-process reactions to agent events.
/// </para>
/// <para>
/// Thread safety: <see cref="Subscribe"/> and <see cref="PublishAsync"/> are safe
/// for concurrent use. The subscriber list is stored as an <see cref="ImmutableArray{T}"/>
/// and mutated under a short lock; publish snapshots the array (no lock held while
/// handlers run) so subscribing or unsubscribing from inside a handler is safe
/// and affects only future invocations.
/// </para>
/// </remarks>
public sealed class InMemoryAgentEventBus : IAgentEventBus
{
    private readonly object _syncRoot = new();
    private readonly ILogger<InMemoryAgentEventBus> _logger;

    private ImmutableArray<Func<AgentEvent, CancellationToken, ValueTask>> _handlers
        = ImmutableArray<Func<AgentEvent, CancellationToken, ValueTask>>.Empty;

    /// <summary>Create a bus. A null-logger is used if none is supplied.</summary>
    public InMemoryAgentEventBus(ILogger<InMemoryAgentEventBus>? logger = null)
    {
        _logger = logger ?? NullLogger<InMemoryAgentEventBus>.Instance;
    }

    /// <inheritdoc />
    public async ValueTask PublishAsync(AgentEvent @event, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(@event);

        // Snapshot under no lock — ImmutableArray read is atomic, and any concurrent
        // Subscribe/Unsubscribe operates on a new immutable array without disturbing
        // this snapshot.
        var snapshot = _handlers;
        if (snapshot.IsDefaultOrEmpty)
        {
            return;
        }

        foreach (var handler in snapshot)
        {
            try
            {
                await handler(@event, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Caller asked to stop — stop. A canceled handler is not a "failed subscriber".
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Agent-event subscriber threw for event type {EventType}; swallowed.",
                    @event.GetType().Name);
            }
        }
    }

    /// <inheritdoc />
    public IDisposable Subscribe(Func<AgentEvent, CancellationToken, ValueTask> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_syncRoot)
        {
            _handlers = _handlers.Add(handler);
        }
        return new Subscription(this, handler);
    }

    private void Unsubscribe(Func<AgentEvent, CancellationToken, ValueTask> handler)
    {
        lock (_syncRoot)
        {
            _handlers = _handlers.Remove(handler);
        }
    }

    private sealed class Subscription : IDisposable
    {
        private readonly InMemoryAgentEventBus _bus;
        private Func<AgentEvent, CancellationToken, ValueTask>? _handler;

        public Subscription(InMemoryAgentEventBus bus, Func<AgentEvent, CancellationToken, ValueTask> handler)
        {
            _bus = bus;
            _handler = handler;
        }

        public void Dispose()
        {
            var handler = Interlocked.Exchange(ref _handler, null);
            if (handler is not null)
            {
                _bus.Unsubscribe(handler);
            }
        }
    }
}
