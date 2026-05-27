// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Core;

/// <summary>
/// Generates and validates short-lived bearer tokens scoped to a plugin invocation.
/// Used by both container and subprocess plugin hosts.
/// </summary>
public interface ICallTokenService
{
    /// <summary>
    /// Generates a token whose lifetime is exactly <paramref name="ttlSeconds"/> seconds.
    /// The caller owns any safety margin: the per-invoke path passes the kill-timeout + 30,
    /// while a long-lived session passes a short, renewable TTL (see the renewal endpoint).
    /// </summary>
    string Generate(string runId, string agentId, int ttlSeconds);

    /// <summary>
    /// Generates a session-mode token that additionally carries <paramref name="leaseId"/>. The gateway
    /// honours such a token only while the matching invoke lease is live (see <see cref="IInvokeLeaseStore"/>).
    /// </summary>
    string Generate(string runId, string agentId, string leaseId, int ttlSeconds);

    /// <summary>Returns true when the token is valid, unexpired, and matches runId + agentId.</summary>
    bool Validate(string token, string runId, string agentId);

    /// <summary>
    /// Validates the token's HMAC and expiry, then extracts the embedded runId and agentId.
    /// Returns false if the token is malformed, tampered with, or expired.
    /// </summary>
    bool TryExtract(string token, out string runId, out string agentId);

    /// <summary>
    /// Like <see cref="TryExtract(string, out string, out string)"/>, but also extracts the embedded
    /// <paramref name="leaseId"/>. <paramref name="leaseId"/> is empty for a non-session (v1) token.
    /// </summary>
    bool TryExtract(string token, out string runId, out string agentId, out string leaseId);

    /// <summary>
    /// Generates a token whose payload additionally carries the per-call <see cref="AgentContextClaims"/>.
    /// Used by the runtime to forward the calling grain's <c>AgentContext</c> to a container plugin so
    /// the plugin's gateway callbacks see the same policy fields (<c>Scopes</c>, <c>PrivilegeLevel</c>,
    /// <c>AllowedTools</c>, …) as in-process agents. Closes the G4 propagation gap.
    /// </summary>
    string Generate(string runId, string agentId, AgentContextClaims claims, int ttlSeconds);

    /// <summary>
    /// Session-mode variant of the claims-bearing <see cref="Generate(string, string, AgentContextClaims, int)"/>:
    /// the token additionally carries <paramref name="leaseId"/> and is honoured only while the matching
    /// invoke lease is live (see <see cref="IInvokeLeaseStore"/>).
    /// </summary>
    string Generate(string runId, string agentId, string leaseId, AgentContextClaims claims, int ttlSeconds);

    /// <summary>
    /// Like <see cref="TryExtract(string, out string, out string, out string)"/>, but also returns the
    /// embedded <paramref name="claims"/>. <paramref name="claims"/> is <c>null</c> for legacy two-segment
    /// (v2) tokens that were minted before claims-propagation landed — handlers should fall back to the
    /// header-only context shape in that case (backwards-compatible during rollout).
    /// </summary>
    bool TryExtract(string token, out string runId, out string agentId, out string leaseId, out AgentContextClaims? claims);
}
