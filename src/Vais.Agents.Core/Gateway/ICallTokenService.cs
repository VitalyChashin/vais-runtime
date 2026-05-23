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
}
