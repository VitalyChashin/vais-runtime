// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;

namespace Vais.Agents;

/// <summary>
/// A request from a guardrail or tool to pause the agent loop and hand control
/// back to the caller for human-in-the-loop (HITL) input. Shipped as a v0.4
/// primitive for consumers to model approval flows, external confirmations,
/// or decision gates; durable mid-loop resume lands later with the
/// durable-execution pillar.
/// </summary>
/// <param name="InterruptId">
/// Stable identifier for this interrupt. Correlates the raise site with the
/// eventual <see cref="ResumeInput.InterruptId"/> the caller supplies on resume.
/// </param>
/// <param name="Reason">Short operator-readable reason for the interrupt.</param>
/// <param name="Payload">
/// Arbitrary JSON payload describing what the caller is being asked to decide —
/// e.g. a tool-call preview for approval, the arguments a guardrail flagged for
/// review. Consumers pick the shape; the library treats it as opaque.
/// </param>
public sealed record AgentInterrupt(string InterruptId, string Reason, JsonElement Payload);
