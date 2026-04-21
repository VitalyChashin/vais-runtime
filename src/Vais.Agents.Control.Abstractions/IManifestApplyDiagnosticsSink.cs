// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Control;

/// <summary>
/// Lightweight sink for non-fatal diagnostics that the manifest translator
/// (and, later, the manifest validator) emit during apply / update flows.
/// The HTTP surface layers a concrete sink on top so warnings surface on
/// the apply response body; hosts without that layering get a no-op.
/// </summary>
/// <remarks>
/// Shipped with v0.18 Pillar C primarily for the
/// <c>handler-and-declarative-fields-both-set</c> apply-time warning but
/// shaped to accept any future URN-identified WARN that doesn't warrant an
/// exception.
/// </remarks>
public interface IManifestApplyDiagnosticsSink
{
    /// <summary>Record a warning keyed to the agent id being applied / updated.</summary>
    /// <param name="agentId">The agent id from <c>AgentManifest.Id</c>.</param>
    /// <param name="urn"><c>urn:vais-agents:*</c> describing the warning class.</param>
    /// <param name="detail">Human-readable detail. Safe to include in HTTP response bodies.</param>
    void Record(string agentId, string urn, string detail);
}
