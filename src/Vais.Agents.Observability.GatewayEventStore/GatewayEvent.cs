// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Observability.GatewayEventStore;

/// <summary>A single LLM completion event emitted by a gateway middleware instance.</summary>
/// <param name="EventId">Unique identifier for this event.</param>
/// <param name="GatewayId">Identifier of the gateway that produced this event (set at <c>AddGatewayEventStore</c> time).</param>
/// <param name="EventKind">Event type: <c>completion.completed</c> or <c>completion.failed</c>.</param>
/// <param name="ModelId">Model identifier returned by the provider; <see langword="null"/> when not reported or on failure.</param>
/// <param name="InputTokens">Prompt token count; 0 when not available.</param>
/// <param name="OutputTokens">Completion token count; 0 when not available.</param>
/// <param name="DurationMs">Wall-clock duration in milliseconds.</param>
/// <param name="CacheHit"><see langword="true"/> when served from a semantic cache; <see langword="null"/> when not applicable.</param>
/// <param name="ErrorType">Exception type name when <paramref name="EventKind"/> is <c>completion.failed</c>; otherwise <see langword="null"/>.</param>
/// <param name="At">UTC timestamp when the event occurred.</param>
/// <param name="CorrelationId">Ambient correlation ID from the agent context at the time of the call; <see langword="null"/> when not set.</param>
/// <param name="RunId">Graph run ID when the call originated inside a graph run; otherwise <see langword="null"/>.</param>
public sealed record GatewayEvent(
    string EventId,
    string GatewayId,
    string EventKind,
    string? ModelId,
    int InputTokens,
    int OutputTokens,
    long? DurationMs,
    bool? CacheHit,
    string? ErrorType,
    DateTimeOffset At,
    string? CorrelationId,
    string? RunId);
