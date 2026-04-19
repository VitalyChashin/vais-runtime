// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents;

/// <summary>
/// Declarative reference to a Model Context Protocol (MCP) server. An MCP server is a
/// <em>source</em> of tools (potentially many), distinct from <see cref="ToolRef"/>
/// which names a single tool. The runtime resolves the transport + connection at
/// agent activation and hands the tools into the agent's tool registry via
/// <c>McpToolSource</c>.
/// </summary>
/// <param name="Name">
/// Stable name for this MCP binding — used for logging, audit, and the optional
/// <see cref="Tools"/> allowlist reference from <see cref="ToolRef.Source"/>.
/// </param>
/// <param name="Transport">One of <c>"stdio"</c>, <c>"streamableHttp"</c>, <c>"sse"</c>.</param>
/// <param name="Command">Executable path for <c>stdio</c> transport. Required when <see cref="Transport"/> = <c>"stdio"</c>.</param>
/// <param name="Args">Command-line arguments for <c>stdio</c>.</param>
/// <param name="Url">Server URL for <c>streamableHttp</c> / <c>sse</c>. Required for those transports.</param>
/// <param name="Env">Environment variables passed to <c>stdio</c> child processes.</param>
/// <param name="AuthRef">Optional <c>secret://</c> URI for bearer / header auth on HTTP transports.</param>
/// <param name="Tools">
/// Optional allowlist restricting which tools from the server are exposed to the agent.
/// Null / empty = expose all tools the server lists.
/// </param>
public sealed record McpServerRef(
    string Name,
    string Transport,
    string? Command = null,
    IReadOnlyList<string>? Args = null,
    string? Url = null,
    IReadOnlyDictionary<string, string>? Env = null,
    string? AuthRef = null,
    IReadOnlyList<string>? Tools = null);
