// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.ScriptRuntime;

/// <summary>
/// Host configuration for code-mode: where the ScriptRuntime sidecar lives and which gateway
/// the script's tool calls route back to. Populated by the runtime host from its options/env.
/// </summary>
public sealed class ScriptRuntimeOptions
{
    /// <summary>Base URL of the supervised ScriptRuntime sidecar. <c>v1/script/run</c> is appended.</summary>
    public string SidecarBaseUrl { get; set; } = "http://localhost:8090";

    /// <summary>
    /// Base URL of the runtime's own container gateway. A script's tool calls post to
    /// <c>{GatewayBaseUrl}/v1/container-gateway/tools/invoke</c> (the same endpoint container plugins use).
    /// </summary>
    public string GatewayBaseUrl { get; set; } = "http://localhost:8080";
}
