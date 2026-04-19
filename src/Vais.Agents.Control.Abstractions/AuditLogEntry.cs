// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Control;

/// <summary>
/// A single record emitted to <see cref="IAuditLog"/> after every lifecycle verb —
/// whether allowed or denied, whether successful or failed. Consumer-facing
/// durable-ish record of who did what, when, and with what outcome.
/// </summary>
/// <param name="At">UTC timestamp when the verb resolved.</param>
/// <param name="Operation">Which lifecycle verb was attempted.</param>
/// <param name="AgentId">Target agent id — null on Create before the id is assigned, though in practice always set.</param>
/// <param name="AgentVersion">Target agent version — null when not applicable (e.g. Query with no version).</param>
/// <param name="PrincipalId">Caller id from <see cref="AgentPrincipal.Id"/>, or <c>"anonymous"</c> when no principal was established.</param>
/// <param name="TenantId">Caller tenant id, when available.</param>
/// <param name="Allowed">True when the policy engine allowed the verb; false when denied.</param>
/// <param name="DenyReason">Populated when <paramref name="Allowed"/> is false — the <see cref="PolicyDecision.Reason"/> the policy engine returned.</param>
/// <param name="ErrorType">Populated when the verb allowed but threw — short exception type name.</param>
public sealed record AuditLogEntry(
    DateTimeOffset At,
    PolicyOperation Operation,
    string? AgentId,
    string? AgentVersion,
    string PrincipalId,
    string? TenantId,
    bool Allowed,
    string? DenyReason,
    string? ErrorType);
