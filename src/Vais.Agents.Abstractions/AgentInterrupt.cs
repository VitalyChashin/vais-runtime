// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;

namespace Vais.Agents;

/// <summary>
/// A request from a guardrail or tool to pause the agent loop and hand control
/// back to the caller for human-in-the-loop (HITL) input.
/// </summary>
/// <remarks>
/// <para>
/// In v0.5, the agent stamps <see cref="RunId"/> before raising this interrupt so
/// the caller can thread it back into <see cref="ResumeInput.RunId"/> on resume.
/// Doing so lets the dispatcher cache-replay any tool calls that completed before
/// the interrupt fired, avoiding side-effect duplication.
/// </para>
/// </remarks>
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
public sealed record AgentInterrupt(string InterruptId, string Reason, JsonElement Payload)
{
    /// <summary>
    /// The run this interrupt was raised inside. Populated by <c>StatefulAiAgent</c>
    /// before throwing <see cref="AgentInterruptedException"/>; guardrail authors
    /// don't set this themselves. Callers round-trip this value into
    /// <see cref="ResumeInput.RunId"/> to enable durable resume.
    /// </summary>
    public string? RunId { get; init; }
}
