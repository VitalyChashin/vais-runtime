// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents;

/// <summary>
/// Maps an inbound bearer token (API key) to an <see cref="AgentContext"/>
/// carrying workspace identity and privilege level.
/// Implement this to enforce per-tenant policy on the OpenAI-compatible transport.
/// </summary>
public interface IInboundIdentityResolver
{
    /// <summary>
    /// Returns a populated <see cref="AgentContext"/> or throws
    /// <see cref="UnauthorizedAccessException"/> if the token is invalid.
    /// </summary>
    ValueTask<AgentContext> ResolveAsync(
        string bearerToken,
        CancellationToken cancellationToken = default);
}
