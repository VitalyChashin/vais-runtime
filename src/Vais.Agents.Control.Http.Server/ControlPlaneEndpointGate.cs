// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Vais.Agents.Control.Http;

/// <summary>
/// Policy + audit gate for the two control-plane kinds that have no lifecycle
/// manager of their own — <c>EvalSuite</c> (registry-direct) and <c>Extension</c>
/// (reloader-direct). The other seven kinds gate inside their lifecycle managers
/// (<c>GateAsync</c>/<c>AuditAsync</c>); this helper applies the same
/// <see cref="IAgentPolicyEngine"/> + <see cref="IAuditLog"/> seam at the endpoint
/// so every mutating control-plane path is authorized and audited uniformly.
/// </summary>
/// <remarks>
/// The gating principal is read from the ambient <see cref="IAgentContextAccessor"/>
/// (populated by <c>UseAgentControlPlanePrincipalMapping</c>), so it carries the
/// caller's JWT scopes — the same principal the lifecycle managers see. Unlike the
/// managers, this endpoint gate audits once at decision time (allow or deny); the
/// "allowed-but-threw" outcome audit stays with the manager-backed kinds.
/// </remarks>
internal static class ControlPlaneEndpointGate
{
    /// <summary>
    /// Evaluate <paramref name="op"/> for the ambient principal and write an audit
    /// entry. Returns <see langword="null"/> when allowed (the caller proceeds), or a
    /// 403 Problem Details <see cref="IResult"/> when denied (already audited).
    /// </summary>
    public static async Task<IResult?> CheckAsync(
        HttpContext http,
        PolicyOperation op,
        string? resourceId,
        string? version,
        CancellationToken ct)
    {
        var policy = http.RequestServices.GetService<IAgentPolicyEngine>() ?? NullAgentPolicyEngine.Instance;
        var principal = BuildPrincipal(http);

        var decision = await policy.EvaluateAsync(op, manifest: null, principal, ct).ConfigureAwait(false);

        await SafeAuditAsync(
            http, op, resourceId, version, principal,
            allowed: decision.IsAllowed,
            denyReason: decision.IsAllowed ? null : (decision.Reason ?? "policy denied")).ConfigureAwait(false);

        if (decision.IsAllowed)
        {
            return null;
        }

        return ProblemDetailsMapping.ToResult(
            new AgentPolicyDeniedException(op, decision.Reason ?? "policy denied"),
            http.Request.Path,
            resourceId,
            op);
    }

    private static AgentPrincipal? BuildPrincipal(HttpContext http)
    {
        var ctx = http.RequestServices.GetService<IAgentContextAccessor>()?.Current ?? AgentContext.Empty;
        return ctx.UserId is { Length: > 0 } userId
            ? new AgentPrincipal(userId, ctx.TenantId, ctx.Scopes)
            : null;
    }

    private static async Task SafeAuditAsync(
        HttpContext http,
        PolicyOperation op,
        string? resourceId,
        string? version,
        AgentPrincipal? principal,
        bool allowed,
        string? denyReason)
    {
        var audit = http.RequestServices.GetService<IAuditLog>() ?? NullAuditLog.Instance;
        try
        {
            await audit.AppendAsync(new AuditLogEntry(
                At: DateTimeOffset.UtcNow,
                Operation: op,
                AgentId: resourceId,
                AgentVersion: version,
                PrincipalId: principal?.Id ?? "anonymous",
                TenantId: principal?.TenantId,
                Allowed: allowed,
                DenyReason: denyReason,
                ErrorType: null)).ConfigureAwait(false);
        }
        catch
        {
            // Audit-write failures must not break the verb.
        }
    }
}
