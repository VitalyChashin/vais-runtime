// Copyright (c) 2026 VAIS2 Platform contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais2.Agents;

/// <summary>
/// Thrown from the agent loop when a guardrail or tool dispatcher raises an
/// <see cref="AgentInterrupt"/>. Callers catch this to know they need human
/// input, gather a <see cref="ResumeInput"/>, and invoke the agent's resume
/// entry point.
/// </summary>
/// <remarks>
/// Distinct from <see cref="AgentGuardrailDeniedException"/>. A denial means
/// the turn is refused outright; an interrupt means "I need someone to decide
/// before this proceeds." The event bus observes both: denials emit
/// <see cref="GuardrailTriggered"/>, interrupts emit <see cref="InterruptRaised"/>.
/// Both still fire <see cref="TurnFailed"/> for the whole turn.
/// </remarks>
public sealed class AgentInterruptedException : Exception
{
    /// <summary>The interrupt payload describing what the caller is being asked to decide.</summary>
    public AgentInterrupt Interrupt { get; }

    /// <summary>Construct an exception carrying the interrupt request.</summary>
    public AgentInterruptedException(AgentInterrupt interrupt)
        : base($"Agent interrupted: {interrupt?.Reason ?? "no reason supplied"}")
    {
        ArgumentNullException.ThrowIfNull(interrupt);
        Interrupt = interrupt;
    }
}
