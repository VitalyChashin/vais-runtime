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
    /// Generates a token valid for <paramref name="timeoutSeconds"/> + 30 seconds.
    /// </summary>
    string Generate(string runId, string agentId, int timeoutSeconds);

    /// <summary>Returns true when the token is valid, unexpired, and matches runId + agentId.</summary>
    bool Validate(string token, string runId, string agentId);

    /// <summary>
    /// Validates the token's HMAC and expiry, then extracts the embedded runId and agentId.
    /// Returns false if the token is malformed, tampered with, or expired.
    /// </summary>
    bool TryExtract(string token, out string runId, out string agentId);
}
