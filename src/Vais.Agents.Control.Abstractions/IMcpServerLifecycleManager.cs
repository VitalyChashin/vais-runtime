// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Control;

/// <summary>
/// Control-plane lifecycle manager for <see cref="IMcpServerRegistry"/>-hosted
/// MCP server manifests (physical and virtual).
/// </summary>
/// <remarks>
/// All verbs are policy-gated (via <see cref="IAgentPolicyEngine"/>) and audited
/// (via <see cref="IAuditLog"/>). Structural validation (physical/virtual field
/// consistency) is performed in <see cref="CreateAsync"/> and <see cref="UpdateAsync"/>,
/// returning 422 on failure.
/// </remarks>
public interface IMcpServerLifecycleManager
{
    /// <summary>Register a new MCP server manifest. Throws on id+version collision (409).</summary>
    ValueTask<McpServerHandle> CreateAsync(
        McpServerManifest manifest, CancellationToken ct = default);

    /// <summary>Replace an existing manifest's definition. Version must differ from <paramref name="handle"/>.</summary>
    ValueTask<McpServerHandle> UpdateAsync(
        McpServerHandle handle, McpServerManifest newManifest, CancellationToken ct = default);

    /// <summary>Return current status for the server.</summary>
    /// <exception cref="McpServerHandleNotFoundException">Server handle was not found in the registry.</exception>
    ValueTask<McpServerStatus> QueryAsync(
        McpServerHandle handle, CancellationToken ct = default);

    /// <summary>Enumerate all registered servers, optionally filtered by a label prefix.</summary>
    IAsyncEnumerable<McpServerManifest> ListAsync(
        string? labelPrefix = null, CancellationToken ct = default);

    /// <summary>Remove the server manifest from the registry.</summary>
    /// <exception cref="McpServerHandleNotFoundException">Server handle was not found.</exception>
    ValueTask EvictAsync(McpServerHandle handle, CancellationToken ct = default);
}
