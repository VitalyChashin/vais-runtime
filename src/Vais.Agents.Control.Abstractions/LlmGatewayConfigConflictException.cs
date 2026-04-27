// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Control;

/// <summary>
/// Thrown when <see cref="ILlmGatewayConfigLifecycleManager.CreateAsync"/> is called for an
/// (id, version) pair that is already registered. HTTP layer maps to 409 with URN
/// <c>urn:vais-agents:llm-gateway-config-conflict</c>.
/// </summary>
public sealed class LlmGatewayConfigConflictException : Exception
{
    /// <summary>Config identifier that already exists.</summary>
    public string ConfigId { get; }

    /// <summary>Version that already exists.</summary>
    public string Version { get; }

    /// <inheritdoc cref="LlmGatewayConfigConflictException"/>
    public LlmGatewayConfigConflictException(string configId, string version)
        : base($"LlmGatewayConfig '{configId}' version '{version}' is already registered.")
    {
        ConfigId = configId;
        Version = version;
    }
}
