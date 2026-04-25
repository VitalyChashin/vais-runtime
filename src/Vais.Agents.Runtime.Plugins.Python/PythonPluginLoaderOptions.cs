// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Runtime.Plugins.Python;

/// <summary>
/// Options for <see cref="PythonPluginScanner"/> and the subprocess supervisor
/// (<c>IPythonPluginHost</c>, PR 2). All fields are optional; defaults target
/// the standard <c>/var/lib/vais/plugins/</c> layout with strict ABI enforcement.
/// </summary>
public sealed class PythonPluginLoaderOptions
{
    /// <summary>
    /// Root directory that contains one subfolder per plugin. Each subfolder
    /// must have a <c>plugin.yaml</c> with <c>spec.runtime: python</c> to be
    /// considered by the Python loader. Defaults to <c>/var/lib/vais/plugins</c>.
    /// </summary>
    public string PluginsDirectory { get; init; } = "/var/lib/vais/plugins";

    /// <summary>
    /// ABI version the runtime accepts. Plugins whose
    /// <c>[tool.vais.plugin].targetApiVersion</c> does not match are skipped with
    /// <see cref="PythonPluginUrns.AbiMismatch"/>. Defaults to
    /// <see cref="PythonPluginAbi.CurrentVersion"/>.
    /// </summary>
    public string RuntimeAbiVersion { get; init; } = PythonPluginAbi.CurrentVersion;

    /// <summary>
    /// MCP initialize-handshake timeout used when <c>plugin.yaml</c> does not
    /// specify <c>spec.health.handshakeTimeoutSeconds</c>. Defaults to 5 seconds.
    /// </summary>
    public int DefaultHandshakeTimeoutSeconds { get; init; } = 5;

    /// <summary>
    /// Per-invoke timeout used when <c>plugin.yaml</c> does not specify
    /// <c>spec.health.invokeTimeoutSeconds</c>. Applies only to
    /// <see cref="PythonHandlerKind.AgentHandler"/> plugins. Defaults to 60 seconds.
    /// </summary>
    public int DefaultInvokeTimeoutSeconds { get; init; } = 60;

    /// <summary>
    /// When <see langword="true"/>, the subprocess supervisor (PR 2) runs
    /// <c>uv sync --frozen</c> inside the plugin directory if <c>.venv/</c> is
    /// absent at startup. Intended for development workflows; production images
    /// should pre-bake the venv and leave this <see langword="false"/>.
    /// Defaults to <see langword="false"/>.
    /// </summary>
    public bool FallbackUvSync { get; init; } = false;

    /// <summary>
    /// Maximum byte size of the opaque state blob a Python agent-handler plugin may
    /// return in <c>newState</c> on each <c>vais/agent.invoke</c> response.
    /// Turns that exceed this limit are rejected with
    /// <see cref="PythonPluginUrns.AgentStateTooLarge"/>; the previous state is
    /// preserved. Set to 0 to disable the check. Defaults to 1 MiB.
    /// </summary>
    public int MaxAgentStateSizeBytes { get; init; } = 1 * 1024 * 1024;
}
