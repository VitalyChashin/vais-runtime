// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents;

/// <summary>
/// Explicit agent-to-agent handoff signal emitted by an orchestrator or a
/// participant. The outgoing agent declares that it's done and the target agent
/// should take over; optionally with a handoff message and a carry-forward slice
/// of history.
/// </summary>
/// <param name="FromAgent">The agent that is handing off.</param>
/// <param name="ToAgent">The agent expected to take over.</param>
/// <param name="Message">
/// Optional short message explaining the handoff — typically surfaced to the target
/// agent as a user or system turn, depending on the orchestrator's handoff policy.
/// </param>
/// <param name="HistoryToCarry">
/// Optional subset of history to pass to the target agent. Orchestrators may ignore
/// this when their policy is "carry full shared history" (the default for most
/// group-chat patterns); it's provided for handoff orchestrators that want to
/// scope what the target sees.
/// </param>
public sealed record Handoff(
    string FromAgent,
    string ToAgent,
    string? Message = null,
    IReadOnlyList<ChatTurn>? HistoryToCarry = null);
