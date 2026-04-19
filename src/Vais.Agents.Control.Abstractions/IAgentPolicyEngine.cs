// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Control;

/// <summary>
/// Admission-control hook the lifecycle manager consults before every verb. Policy
/// authors implement this interface to gate Create / Invoke / Signal / Update /
/// Cancel / Evict / Query on tenant quotas, label allowlists, identity checks,
/// or any custom rule they choose. The v0.6 shipped default is a no-op allow;
/// real implementations (OPA/Rego, custom) slot in via DI.
/// </summary>
/// <remarks>
/// <para>
/// The engine sees the <see cref="AgentManifest"/> (null on <see cref="PolicyOperation.Query"/>
/// against an unknown agent), the caller <see cref="AgentPrincipal"/> when one was
/// established via identity middleware, and the <see cref="PolicyOperation"/>. It
/// returns a <see cref="PolicyDecision"/> — allow or deny-with-reason.
/// </para>
/// <para>
/// <b>Hot path.</b> Called once per lifecycle verb; keep it fast. Async is on the
/// contract so policy engines backed by out-of-process OPA / network calls are
/// possible; synchronous in-process engines should return <see cref="ValueTask.FromResult{T}(T)"/>.
/// </para>
/// </remarks>
public interface IAgentPolicyEngine
{
    /// <summary>
    /// Decide whether <paramref name="operation"/> on <paramref name="manifest"/> by
    /// <paramref name="principal"/> is allowed. Return <see cref="PolicyDecision.Allow"/>
    /// to permit; <see cref="PolicyDecision.Deny"/> to block with a reason.
    /// </summary>
    ValueTask<PolicyDecision> EvaluateAsync(
        PolicyOperation operation,
        AgentManifest? manifest,
        AgentPrincipal? principal,
        CancellationToken cancellationToken = default);
}
