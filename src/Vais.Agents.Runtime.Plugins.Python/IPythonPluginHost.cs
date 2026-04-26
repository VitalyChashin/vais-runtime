// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using ModelContextProtocol.Client;

namespace Vais.Agents.Runtime.Plugins.Python;

/// <summary>
/// Supervises the full lifecycle of all Python plugin subprocesses in the current silo:
/// spawning, MCP handshake, restart-on-crash, and graceful shutdown.
/// Registered as a singleton <see cref="Microsoft.Extensions.Hosting.IHostedService"/>
/// via <c>AddPythonPlugins</c>.
/// </summary>
public interface IPythonPluginHost
{
    /// <summary>
    /// A snapshot of every plugin the host attempted to load, together with its current
    /// supervisor status and the live <see cref="McpClient"/> when the plugin is
    /// <see cref="PythonPluginStatus.Ready"/>.
    /// </summary>
    IReadOnlyCollection<LoadedPythonPlugin> LoadedPlugins { get; }
}

/// <summary>
/// Describes a single Python plugin that has been loaded (or attempted) by
/// <see cref="IPythonPluginHost"/>.
/// </summary>
/// <param name="Descriptor">Static metadata parsed from <c>plugin.yaml</c> and <c>pyproject.toml</c>.</param>
/// <param name="Status">Current supervisor state.</param>
/// <param name="ProcessId">OS process ID when the subprocess is alive; <see langword="null"/> otherwise.</param>
/// <param name="McpClient">
/// Live MCP client when <paramref name="Status"/> is <see cref="PythonPluginStatus.Ready"/>;
/// <see langword="null"/> in all other states.
/// </param>
public sealed record LoadedPythonPlugin(
    PythonPluginDescriptor Descriptor,
    PythonPluginStatus Status,
    int? ProcessId,
    McpClient? McpClient)
{
    /// <summary>
    /// Last few stderr lines from the most recent subprocess spawn, or <see langword="null"/>
    /// when no output was captured. Surfaced in <c>GET /v1/plugins</c> and <c>vais plugin-status</c>.
    /// </summary>
    public string? LastErrorSnippet { get; init; }
}

/// <summary>Lifecycle states for a supervised Python plugin subprocess.</summary>
public enum PythonPluginStatus
{
    /// <summary>The subprocess is being spawned and the MCP handshake is in progress.</summary>
    Loading = 0,

    /// <summary>The subprocess is running and the MCP handshake completed successfully.</summary>
    Ready = 1,

    /// <summary>The subprocess exited unexpectedly; a restart is being attempted.</summary>
    Restarting = 2,

    /// <summary>
    /// The plugin cannot be used. Either the initial handshake failed, all restart attempts
    /// were exhausted, or <see cref="PythonRestartPolicy.Never"/> is configured.
    /// Any tool call against this plugin returns a <see cref="PythonPluginUrns.Unavailable"/> URN.
    /// </summary>
    Unavailable = 3,
}
