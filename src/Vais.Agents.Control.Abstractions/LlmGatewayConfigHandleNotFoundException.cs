// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Control;

/// <summary>
/// Thrown when a <see cref="LlmGatewayConfigHandle"/> references a config that is not
/// registered in the current <see cref="ILlmGatewayConfigRegistry"/>. HTTP layer maps to
/// 404 with URN <c>urn:vais-agents:llm-gateway-config-handle-not-found</c>.
/// </summary>
public sealed class LlmGatewayConfigHandleNotFoundException : Exception
{
    /// <summary>Config identifier that was not found.</summary>
    public string ConfigId { get; }

    /// <summary>Version that was not found.</summary>
    public string Version { get; }

    /// <inheritdoc cref="LlmGatewayConfigHandleNotFoundException"/>
    public LlmGatewayConfigHandleNotFoundException(string configId, string version)
        : base($"LlmGatewayConfig '{configId}' version '{version}' is not registered.")
    {
        ConfigId = configId;
        Version = version;
    }
}
