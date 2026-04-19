// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Control;

/// <summary>
/// Outcome of an <see cref="IAgentPolicyEngine.EvaluateAsync"/> call. Either
/// <see cref="Allow"/> (let the lifecycle verb proceed) or <see cref="Deny"/>
/// with a human-readable reason (short-circuit with an audit entry + typed
/// exception at the dispatcher seam).
/// </summary>
/// <remarks>
/// Struct-shaped: cheap to allocate on the hot path, stable equality, no null
/// pitfalls. The <see cref="Reason"/> field is only populated when <see cref="IsAllowed"/>
/// is <c>false</c>.
/// </remarks>
public readonly record struct PolicyDecision
{
    private PolicyDecision(bool isAllowed, string? reason)
    {
        IsAllowed = isAllowed;
        Reason = reason;
    }

    /// <summary>True when the policy lets the verb proceed; false when it blocks.</summary>
    public bool IsAllowed { get; }

    /// <summary>Human-readable explanation when <see cref="IsAllowed"/> is false; null otherwise.</summary>
    public string? Reason { get; }

    /// <summary>Shared singleton for the common allow case.</summary>
    public static PolicyDecision Allow { get; } = new(isAllowed: true, reason: null);

    /// <summary>Construct a denial with an operator-readable reason.</summary>
    public static PolicyDecision Deny(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new PolicyDecision(isAllowed: false, reason: reason);
    }
}
