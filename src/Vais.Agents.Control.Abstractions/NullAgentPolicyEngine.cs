// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Control;

/// <summary>
/// Allow-everything <see cref="IAgentPolicyEngine"/>. The default when the lifecycle
/// manager's DI wiring doesn't specify a policy engine — preserves pre-v0.6
/// behaviour (no policy gating) while keeping the middleware seam in place for
/// real policy engines (OPA/Rego, custom) to slot in without engine changes.
/// </summary>
public sealed class NullAgentPolicyEngine : IAgentPolicyEngine
{
    /// <summary>Shared singleton instance. Stateless.</summary>
    public static readonly NullAgentPolicyEngine Instance = new();

    private NullAgentPolicyEngine() { }

    /// <inheritdoc />
    public ValueTask<PolicyDecision> EvaluateAsync(
        PolicyOperation operation,
        AgentManifest? manifest,
        AgentPrincipal? principal,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult(PolicyDecision.Allow);
}
