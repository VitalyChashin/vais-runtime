// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Runtime.Extensions;

/// <summary>
/// Composes per-agent middleware chains from the set of loaded extensions whose scope
/// matches the agent. Chains are cached per agent and invalidated on extension swap.
/// Phase A supports <see cref="AgentInputMiddleware"/> and <see cref="AgentOutputMiddleware"/>;
/// additional seams are added in Phase B+.
/// </summary>
public interface IExtensionChainComposer
{
    /// <summary>
    /// Returns the ordered <see cref="AgentInputMiddleware"/> chain for <paramref name="agentId"/>.
    /// Empty when no extensions scope to this agent on the <c>agentInput</c> seam.
    /// </summary>
    Task<IReadOnlyList<AgentInputMiddleware>> GetInputChainAsync(string agentId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the ordered <see cref="AgentOutputMiddleware"/> chain for <paramref name="agentId"/>.
    /// Empty when no extensions scope to this agent on the <c>agentOutput</c> seam.
    /// </summary>
    Task<IReadOnlyList<AgentOutputMiddleware>> GetOutputChainAsync(string agentId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the ordered <see cref="ToolGatewayMiddleware"/> chain for <paramref name="agentId"/>.
    /// Empty when no extensions scope to this agent on the <c>toolGatewayMiddleware</c> seam.
    /// Consumers concatenate this after statically-registered tool gateway middleware.
    /// </summary>
    Task<IReadOnlyList<ToolGatewayMiddleware>> GetToolChainAsync(string agentId, CancellationToken cancellationToken = default);

    /// <summary>Invalidate the cached chain for <paramref name="agentId"/>.</summary>
    void InvalidateAgent(string agentId);

    /// <summary>Invalidate all cached chains. Called on every extension swap or unload.</summary>
    void InvalidateAll();
}
