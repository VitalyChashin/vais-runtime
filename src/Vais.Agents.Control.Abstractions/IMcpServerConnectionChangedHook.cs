// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Control;

/// <summary>
/// Observer invoked by the physical MCP server connection service when a
/// registered server connects or disconnects. Register implementations in DI as
/// <c>IEnumerable&lt;IMcpServerConnectionChangedHook&gt;</c> — all registered
/// hooks are dispatched after the connection state changes.
/// Exceptions from hook implementations are logged and swallowed;
/// state is committed before hooks run.
/// </summary>
public interface IMcpServerConnectionChangedHook
{
    /// <summary>Called once after a physical MCP server successfully connects and its
    /// <see cref="Vais.Agents.IToolSource"/> is ready.</summary>
    Task OnConnectedAsync(string serverId, CancellationToken cancellationToken = default);

    /// <summary>Called once after a physical MCP server is detected as disconnected
    /// (on service stop or reconnect attempt before re-establishment).</summary>
    Task OnDisconnectedAsync(string serverId, CancellationToken cancellationToken = default);
}
