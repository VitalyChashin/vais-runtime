// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Runtime.Plugins.Python;

/// <summary>
/// Loaded-plugin descriptor produced by <see cref="PythonPluginScanner"/> for each
/// successfully parsed Python plugin subfolder. Captures everything the subprocess
/// supervisor (PR 2) needs to spawn the interpreter and wire up the MCP stdio connection.
/// </summary>
/// <param name="Name">Friendly plugin name from <c>metadata.name</c> in <c>plugin.yaml</c>,
/// falling back to the subfolder name.</param>
/// <param name="PluginDirectory">Absolute path to the plugin's root directory
/// (the subfolder that contains <c>plugin.yaml</c>).</param>
/// <param name="InterpreterPath">Absolute path to the Python interpreter binary.
/// Resolved from the relative <c>spec.python.interpreter</c> in <c>plugin.yaml</c>
/// (e.g. <c>.venv/bin/python</c>) at scan time.</param>
/// <param name="EntrypointPath">Absolute path to the MCP server entrypoint script
/// (e.g. <c>src/research_planner/server.py</c>), resolved from
/// <c>spec.entrypoint</c> in <c>plugin.yaml</c>.</param>
/// <param name="TargetApiVersion">The <c>targetApiVersion</c> value from
/// <c>[tool.vais.plugin]</c> in <c>pyproject.toml</c> (e.g. <c>"0.23"</c>).</param>
/// <param name="HandshakeTimeoutSeconds">MCP initialize-handshake budget in seconds,
/// from <c>spec.health.handshakeTimeoutSeconds</c> or the loader default.</param>
/// <param name="RestartPolicy">Subprocess restart policy on crash.</param>
/// <param name="DeclaredTools">Tool names declared in <c>[tool.vais.plugin].tools</c>.
/// The supervisor validates this list against <c>tools/list</c> after handshake.</param>
/// <param name="SecretRefs">Environment-variable secret references for the subprocess.
/// Populated by the runtime host (PR 3); empty at scan time.</param>
public sealed record PythonPluginDescriptor(
    string Name,
    string PluginDirectory,
    string InterpreterPath,
    string EntrypointPath,
    string TargetApiVersion,
    int HandshakeTimeoutSeconds,
    PythonRestartPolicy RestartPolicy,
    IReadOnlyList<string> DeclaredTools,
    IReadOnlyDictionary<string, string> SecretRefs);
