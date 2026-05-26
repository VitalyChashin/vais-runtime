// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;

namespace Vais.Agents;

/// <summary>
/// Canonical Plan D payload for tool-call trajectory events. Carry this in
/// <see cref="InterceptorTeeEvent.Payload"/> so the recording adapter
/// (<c>RecordingInterceptorTee</c> in <c>Vais.Agents.Core</c>) can project the full typed
/// <see cref="TrajectoryEvent"/>; loose payloads (or null) still produce a valid trajectory
/// event with the fields the adapter can infer from <see cref="InterceptionContext"/> alone.
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
