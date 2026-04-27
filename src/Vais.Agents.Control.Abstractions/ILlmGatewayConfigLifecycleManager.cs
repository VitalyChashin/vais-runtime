// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Control;

/// <summary>
/// Control-plane lifecycle manager for <see cref="ILlmGatewayConfigRegistry"/>-hosted
/// LLM gateway configs.
/// </summary>
/// <remarks>
/// All verbs are policy-gated (via <see cref="IAgentPolicyEngine"/>) and audited
/// (via <see cref="IAuditLog"/>). Gateway config objects have no runtime execution
/// surface, so there is no InvokeAsync, SignalAsync, or CancelAsync.
/// </remarks>
public interface ILlmGatewayConfigLifecycleManager
{
    /// <summary>Register a new LLM gateway config manifest. Throws on id+version collision (409).</summary>
    ValueTask<LlmGatewayConfigHandle> CreateAsync(
        LlmGatewayConfigManifest manifest, CancellationToken ct = default);

    /// <summary>Replace an existing manifest's definition. Version must differ from <paramref name="handle"/>.</summary>
    ValueTask<LlmGatewayConfigHandle> UpdateAsync(
        LlmGatewayConfigHandle handle, LlmGatewayConfigManifest newManifest, CancellationToken ct = default);

    /// <summary>Return current status for the config.</summary>
    /// <exception cref="LlmGatewayConfigHandleNotFoundException">Config handle was not found in the registry.</exception>
    ValueTask<LlmGatewayConfigStatus> QueryAsync(
        LlmGatewayConfigHandle handle, CancellationToken ct = default);

    /// <summary>Enumerate all registered configs, optionally filtered by a label prefix.</summary>
    IAsyncEnumerable<LlmGatewayConfigManifest> ListAsync(
        string? labelPrefix = null, CancellationToken ct = default);

    /// <summary>Remove the config manifest from the registry.</summary>
    /// <exception cref="LlmGatewayConfigHandleNotFoundException">Config handle was not found.</exception>
    ValueTask EvictAsync(LlmGatewayConfigHandle handle, CancellationToken ct = default);
}
