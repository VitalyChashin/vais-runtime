// Copyright (c) 2026 VAIS2 Platform contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais2.Agents;

/// <summary>
/// Outcome class for a guardrail evaluation. Deliberately minimal in v0.4; additional
/// values (<c>Replace</c>, <c>Interrupt</c>) will land with later pillars when the
/// corresponding execution-loop primitives exist.
/// </summary>
public enum GuardrailDecision
{
    /// <summary>The turn may proceed.</summary>
    Pass = 0,

    /// <summary>
    /// The turn is denied. <c>StatefulAiAgent</c> raises <see cref="AgentGuardrailDeniedException"/>
    /// and treats the turn as failed (usage sink sees <c>Succeeded = false</c>, event bus sees
    /// <see cref="TurnFailed"/>).
    /// </summary>
    Deny = 1,
}
