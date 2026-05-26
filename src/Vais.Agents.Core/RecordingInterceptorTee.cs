// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Vais.Agents.Core;

/// <summary>
/// Plan D's default <see cref="IInterceptorTee"/> consumer. Adapts the loose
/// <see cref="InterceptorTeeEvent"/> shape (from C1) into a typed <see cref="TrajectoryEvent"/>
/// and appends it to the registered <see cref="IInterceptorTeeStore"/>. Replaces
/// <see cref="NullInterceptorTee"/> in any composition root that wires a store.
/// </summary>
/// <remarks>
/// Fire-and-forget contract — append failures are logged at warn level and never thrown
/// back to the producing interceptor (a flaky store must not break the interception
/// lifecycle). The projection knows the small surface of payload shapes Plan D defines:
/// <see cref="ToolCallTrajectoryPayload"/> for south tool calls and the loose payload for
/// everything else.
/// </remarks>
public sealed class RecordingInterceptorTee(
    IInterceptorTeeStore store,
    TrajectoryArgumentRedactor? redactor = null,
    ILogger<RecordingInterceptorTee>? logger = null) : IInterceptorTee
{
    private readonly IInterceptorTeeStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly TrajectoryArgumentRedactor _redactor = redactor ?? TrajectoryArgumentRedactor.Default;
    private readonly ILogger<RecordingInterceptorTee> _logger = logger ?? NullLogger<RecordingInterceptorTee>.Instance;

    /// <inheritdoc />
    public async ValueTask EmitAsync(InterceptorTeeEvent teeEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(teeEvent);
        try
        {
            var trajectory = Project(teeEvent);
            await _store.AppendAsync(trajectory, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "RecordingInterceptorTee: failed to append trajectory event '{EventName}' for agent '{AgentId}'. Continuing.",
                teeEvent.EventName, teeEvent.Context.AgentContext.AgentName);
        }
    }

    private TrajectoryEvent Project(InterceptorTeeEvent teeEvent)
    {
        var ctx = teeEvent.Context;
        var payload = teeEvent.Payload as ToolCallTrajectoryPayload;

        var argShape = payload?.Arguments is { ValueKind: JsonValueKind.Object } args
            ? _redactor.ToShape(args)
            : null;

        return new TrajectoryEvent
        {
            EventId = Guid.CreateVersion7().ToString("N"),
            Timestamp = DateTimeOffset.UtcNow,
            EventName = teeEvent.EventName,
            Operation = ctx.Operation,
            AgentId = ctx.AgentContext.AgentName,
            RunId = ctx.AgentContext.RunId,
            ConceptName = payload?.ConceptName,
            Transport = payload?.Transport,
            ArgumentsShape = argShape,
            Outcome = payload?.Outcome,
            OntologyVersion = ctx.Binding?.OntologyVersion,
            Duration = payload?.Duration,
        };
    }
}

/// <summary>
/// Canonical Plan D payload for tool-call trajectory events. Carry this in
/// <see cref="InterceptorTeeEvent.Payload"/> so <see cref="RecordingInterceptorTee"/> can
/// project the full typed event; loose payloads (or null) still produce a valid trajectory
/// event with the fields it can infer from <see cref="InterceptionContext"/> alone.
/// </summary>
/// <param name="ConceptName">Tool / verb name being intercepted (e.g. <c>tavily_search</c>, <c>vais.validate</c>).</param>
/// <param name="Transport">Routing hint: <c>"north"</c> for design-tools MCP, <c>"south"</c> for tool dispatch.</param>
/// <param name="Arguments">Raw arguments JSON — <see cref="TrajectoryArgumentRedactor"/> redacts before storage; raw values never reach the store.</param>
/// <param name="Outcome">Result categorization (Ok / Error / ShortCircuit + optional error type).</param>
/// <param name="Duration">Wall-clock duration from request to response phase. Null when not yet known (request-phase only).</param>
public sealed record ToolCallTrajectoryPayload(
    string ConceptName,
    string Transport,
    JsonElement Arguments,
    TrajectoryOutcome? Outcome = null,
    TimeSpan? Duration = null);
