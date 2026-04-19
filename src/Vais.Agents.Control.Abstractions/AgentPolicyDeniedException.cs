// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Control;

/// <summary>
/// Thrown by the lifecycle manager when <see cref="IAgentPolicyEngine"/> returns
/// <see cref="PolicyDecision.Deny"/> on a verb. Carries the denied operation and
/// the policy's reason so the HTTP layer can translate into RFC 7807
/// <c>urn:vais-agents:policy-denied</c> Problem Details without losing context.
/// </summary>
public sealed class AgentPolicyDeniedException : Exception
{
    /// <summary>Create a denial exception for <paramref name="operation"/> with <paramref name="reason"/>.</summary>
    public AgentPolicyDeniedException(PolicyOperation operation, string reason)
        : base($"Policy denied {operation}: {reason}")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        Operation = operation;
        Reason = reason;
    }

    /// <summary>The lifecycle verb that was blocked.</summary>
    public PolicyOperation Operation { get; }

    /// <summary>The policy engine's operator-readable denial reason.</summary>
    public string Reason { get; }
}
