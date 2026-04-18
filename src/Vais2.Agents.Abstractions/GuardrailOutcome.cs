// Copyright (c) 2026 VAIS2 Platform contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais2.Agents;

/// <summary>
/// Return shape for every guardrail evaluation. Pair with <see cref="Pass"/> for the
/// common allow path and <see cref="Deny"/> for denial with an operator-readable reason.
/// </summary>
/// <param name="Decision">Pass or Deny.</param>
/// <param name="Reason">
/// Human-readable explanation. Surfaced on <see cref="AgentGuardrailDeniedException.Reason"/>
/// when the outcome is <see cref="GuardrailDecision.Deny"/>; on <see cref="TurnFailed.ErrorMessage"/>
/// in the event stream.
/// </param>
public sealed record GuardrailOutcome(GuardrailDecision Decision, string? Reason = null)
{
    /// <summary>Shared singleton for the allow path.</summary>
    public static GuardrailOutcome Pass { get; } = new(GuardrailDecision.Pass);

    /// <summary>Factory for a denial outcome with an optional reason.</summary>
    public static GuardrailOutcome Deny(string? reason = null) => new(GuardrailDecision.Deny, reason);
}
