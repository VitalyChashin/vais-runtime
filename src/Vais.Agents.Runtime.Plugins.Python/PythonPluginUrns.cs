// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Runtime.Plugins.Python;

/// <summary>
/// URNs emitted by the Python plugin scanner and subprocess supervisor.
/// Non-fatal per-plugin failures land in runtime logs; the supervisor surfaces
/// <see cref="Unavailable"/> to callers when no functioning subprocess exists.
/// </summary>
public static class PythonPluginUrns
{
    /// <summary>Common prefix for all URNs emitted from the Python plugin subsystem.</summary>
    public const string UrnPrefix = "urn:vais-agents:";

    /// <summary>
    /// Plugin directory exists but the scanner could not produce a valid descriptor —
    /// missing or unreadable <c>plugin.yaml</c> / <c>pyproject.toml</c>, YAML/TOML
    /// parse error, missing required fields, or interpreter path escaping the plugin dir.
    /// Non-fatal: the runtime continues loading other plugins.
    /// </summary>
    public const string LoadFailed = UrnPrefix + "python-plugin-load-failed";

    /// <summary>
    /// <c>[tool.vais.plugin].targetApiVersion</c> in <c>pyproject.toml</c> does not
    /// match <see cref="PythonPluginAbi.CurrentVersion"/>. Non-fatal: the plugin is
    /// skipped; the runtime continues loading other plugins.
    /// </summary>
    public const string AbiMismatch = UrnPrefix + "python-plugin-abi-mismatch";

    /// <summary>
    /// The Python subprocess exited unexpectedly while idle (not during a tool call).
    /// The supervisor transitions to <c>Restarting</c> state if
    /// <see cref="PythonRestartPolicy.ExponentialBackoff"/> is configured.
    /// </summary>
    public const string Exited = UrnPrefix + "python-plugin-exited";

    /// <summary>
    /// A tool call was dispatched but no functioning subprocess is available
    /// (either <see cref="PythonRestartPolicy.Never"/> and the process crashed, or
    /// back-off retries were exhausted). The tool call fails with this URN as the
    /// error identifier.
    /// </summary>
    public const string Unavailable = UrnPrefix + "python-plugin-unavailable";

    /// <summary>
    /// The MCP <c>initialize</c> handshake did not complete within
    /// <c>spec.health.handshakeTimeoutSeconds</c>. The subprocess is killed and the
    /// plugin is skipped. Non-fatal: the runtime continues loading other plugins.
    /// </summary>
    public const string HandshakeTimeout = UrnPrefix + "python-plugin-handshake-timeout";

    /// <summary>
    /// A plugin subfolder contains both a <c>plugin.yaml</c> with
    /// <c>runtime: python</c> <em>and</em> <c>.dll</c> files that qualify it for
    /// the .NET plugin loader. Both loaders refuse the folder to avoid
    /// split-ownership. Mirrors the
    /// <c>urn:vais-agents:plugin-handler-collision</c> precedent from v0.18.
    /// Non-fatal: the runtime continues loading other plugins.
    /// </summary>
    public const string AmbiguousFolder = UrnPrefix + "python-plugin-ambiguous-folder";
}
