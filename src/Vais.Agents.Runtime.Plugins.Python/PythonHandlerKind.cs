// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Runtime.Plugins.Python;

/// <summary>
/// Discriminates between the two plugin kinds a <c>plugin.yaml</c> may declare.
/// </summary>
public enum PythonHandlerKind
{
    /// <summary>
    /// The plugin acts as an MCP tool server (v0.23 default). The subprocess speaks the
    /// MCP protocol; its tools are exposed to agents via the <c>mcp:&lt;name&gt;</c> source
    /// prefix. No <see cref="IAiAgent"/> is registered.
    /// </summary>
    McpToolServer = 0,

    /// <summary>
    /// The plugin provides a Python-backed <see cref="IAiAgent"/> (v0.24). The subprocess
    /// speaks MCP for the handshake and then responds to <c>vais/agent.*</c> JSON-RPC methods.
    /// A <see cref="PythonAgentShimFactory"/> is registered in <see cref="IPluginHandlerRegistry"/>
    /// under <see cref="PythonPluginDescriptor.HandlerTypeName"/>.
    /// </summary>
    AgentHandler = 1,
}
