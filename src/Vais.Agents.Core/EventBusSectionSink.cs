// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents;

/// <summary>
/// <see cref="ISectionTelemetrySink"/> that publishes a <see cref="RequestSectionsBuilt"/>
/// event on the configured <see cref="IAgentEventBus"/>. Subscribers — Langfuse cross-process
/// dispatchers, custom audit pipelines, eval harnesses — observe section breakdowns through the
/// same event-bus fan-out they already use for <see cref="TurnStarted"/> / <see cref="TurnCompleted"/>.
/// </summary>
/// <remarks>
/// Bus failures are absorbed by the emitter's catch block (per the <see cref="SectionTelemetryEmitter"/>
/// contract: a buggy sink can't break the turn). This sink only wraps the publish call; any
/// resilience strategy (retries, dead-letter, etc.) belongs in the bus or its subscribers.
/// </remarks>
public sealed class EventBusSectionSink : ISectionTelemetrySink
{
    private readonly IAgentEventBus _bus;

    /// <summary>Wrap <paramref name="bus"/> as a section telemetry sink.</summary>
    /// <param name="bus">Target bus. Must not be null.</param>
    public EventBusSectionSink(IAgentEventBus bus)
    {
        ArgumentNullException.ThrowIfNull(bus);
        _bus = bus;
    }

    /// <inheritdoc />
    public ValueTask EmitAsync(SectionTelemetrySnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var @event = new RequestSectionsBuilt(
            At: DateTimeOffset.UtcNow,
            Context: snapshot.Context,
            TurnIndex: snapshot.TurnIndex,
            Sections: snapshot.Sections,
            Budget: snapshot.Budget);

        return _bus.PublishAsync(@event, cancellationToken);
    }
}
