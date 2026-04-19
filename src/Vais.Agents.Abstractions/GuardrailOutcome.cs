// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents;

/// <summary>
/// Return shape for every guardrail evaluation. Pair with <see cref="Pass"/> for the
/// common allow path, <see cref="Deny"/> for denial with an operator-readable reason,
/// or <see cref="Interrupt(AgentInterrupt, string?)"/> to pause for human-in-the-loop
/// review.
/// </summary>
/// <param name="Decision">Pass, Deny, or Interrupt.</param>
/// <param name="Reason">
/// Human-readable explanation. Surfaced on <see cref="AgentGuardrailDeniedException.Reason"/>
/// when the outcome is <see cref="GuardrailDecision.Deny"/>; on <see cref="TurnFailed.ErrorMessage"/>
/// in the event stream.
/// </param>
/// <param name="InterruptPayload">
/// Payload for <see cref="GuardrailDecision.Interrupt"/>. The guardrail describes what
/// the caller is being asked to decide; the caller later resumes with a matching
/// <see cref="ResumeInput"/>. Non-null when <see cref="Decision"/> is
/// <see cref="GuardrailDecision.Interrupt"/>, null otherwise.
/// </param>
public sealed record GuardrailOutcome(GuardrailDecision Decision, string? Reason = null, AgentInterrupt? InterruptPayload = null)
{
    /// <summary>Shared singleton for the allow path.</summary>
    public static GuardrailOutcome Pass { get; } = new(GuardrailDecision.Pass);

    /// <summary>Factory for a denial outcome with an optional reason.</summary>
    public static GuardrailOutcome Deny(string? reason = null) => new(GuardrailDecision.Deny, reason);

    /// <summary>Factory for an interrupt outcome carrying the pause payload.</summary>
    public static GuardrailOutcome Interrupt(AgentInterrupt interrupt, string? reason = null)
    {
        ArgumentNullException.ThrowIfNull(interrupt);
        return new GuardrailOutcome(GuardrailDecision.Interrupt, reason ?? interrupt.Reason, interrupt);
    }
}
