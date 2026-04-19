// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents;

/// <summary>
/// Raised when a guardrail returns <see cref="GuardrailDecision.Deny"/>. Carries the
/// layer that denied and the optional operator-readable reason from the outcome.
/// </summary>
/// <remarks>
/// <c>StatefulAiAgent</c> treats a denial as a regular turn failure: the usage sink
/// receives a record with <c>Succeeded = false</c> and this exception's type name;
/// the event bus sees <see cref="TurnFailed"/> with <c>ErrorMessage</c> set to
/// <see cref="Reason"/> (if supplied, else the exception message).
/// </remarks>
public sealed class AgentGuardrailDeniedException : Exception
{
    /// <summary>Which middleware layer raised the denial.</summary>
    public GuardrailLayer Layer { get; }

    /// <summary>The reason the guardrail supplied with its <see cref="GuardrailOutcome"/>, if any.</summary>
    public string? Reason { get; }

    /// <summary>Create an exception for a specific layer and optional reason.</summary>
    public AgentGuardrailDeniedException(GuardrailLayer layer, string? reason)
        : base(FormatMessage(layer, reason))
    {
        Layer = layer;
        Reason = reason;
    }

    private static string FormatMessage(GuardrailLayer layer, string? reason)
        => reason is null
            ? $"Guardrail ({layer}) denied the turn."
            : $"Guardrail ({layer}) denied the turn: {reason}";
}
