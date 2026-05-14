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

    // -----------------------------------------------------------------------
    // v0.24 — Python agent handler URNs
    // -----------------------------------------------------------------------

    /// <summary>
    /// A <c>vais/agent.invoke</c> call to the Python subprocess failed with an
    /// exception or the subprocess returned a JSON-RPC error response.
    /// The agent turn fails; the session state is unchanged.
    /// </summary>
    public const string AgentInvokeFailed = UrnPrefix + "python-agent-invoke-failed";

    /// <summary>
    /// A <c>vais/agent.invoke</c> call did not complete within
    /// <c>spec.health.invokeTimeoutSeconds</c>. The subprocess is <b>not</b> killed;
    /// it remains Ready for the next call. Three consecutive timeouts escalate to a
    /// subprocess restart.
    /// </summary>
    public const string AgentInvokeTimeout = UrnPrefix + "python-agent-invoke-timeout";

    /// <summary>
    /// The <c>new_state</c> field returned by <c>vais/agent.invoke</c> exceeds the
    /// configured maximum state size (<see cref="PythonPluginLoaderOptions.MaxAgentStateSizeBytes"/>).
    /// The agent turn fails; the previous state is preserved unchanged.
    /// </summary>
    public const string AgentStateTooLarge = UrnPrefix + "python-agent-state-too-large";

    /// <summary>
    /// The Python subprocess returned a malformed JSON-RPC response to a
    /// <c>vais/agent.*</c> call (missing required fields, wrong type, etc.).
    /// The agent turn fails; the session state is unchanged.
    /// </summary>
    public const string AgentProtocolError = UrnPrefix + "python-agent-protocol-error";

    /// <summary>
    /// A Python plugin's <c>handler.typeName</c> collides with a handler already registered
    /// by a .NET plugin or another Python plugin. The Python plugin is marked Unavailable.
    /// Mirrors the <c>urn:vais-agents:plugin-handler-collision</c> precedent from v0.18.
    /// </summary>
    public const string AgentHandlerCollision = UrnPrefix + "python-agent-handler-collision";

    // -----------------------------------------------------------------------
    // v0.31 — Secret propagation URNs
    // -----------------------------------------------------------------------

    /// <summary>
    /// A Python plugin declares secrets in <c>spec.secrets</c> but the runtime could not
    /// resolve one or more secret URIs (missing env var, file not found, unknown scheme,
    /// or no <c>ISecretResolver</c> registered). The plugin is skipped on startup
    /// or the reload is aborted; the running subprocess is unaffected during a reload.
    /// </summary>
    public const string SecretResolutionFailed = UrnPrefix + "python-plugin-secret-resolution-failed";

    // -----------------------------------------------------------------------
    // v0.34 — Python plugin CLI bootstrap URNs
    // -----------------------------------------------------------------------

    /// <summary>
    /// First-push bootstrap failed: <c>python3.11 -m venv</c> or <c>pip install</c>
    /// returned a non-zero exit code inside the runtime container. Check the venv
    /// provisioning logs for the actual error output.
    /// </summary>
    public const string BootstrapFailed = UrnPrefix + "python-plugin-bootstrap-failed";

    // -----------------------------------------------------------------------
    // v0.25 — Python plugin hot-reload URNs
    // -----------------------------------------------------------------------

    /// <summary>
    /// A hot-reload was triggered but the new <c>plugin.yaml</c> specifies a different
    /// <c>handler.typeName</c> than the running plugin. In-place reload is not
    /// supported when the handler identity changes; a silo restart is required.
    /// </summary>
    public const string ReloadHandlerTypeNameChanged = UrnPrefix + "python-reload-handler-type-name-changed";

    /// <summary>
    /// A hot-reload was triggered but the new subprocess failed its MCP handshake.
    /// The plugin transitions to <see cref="PythonPluginStatus.Unavailable"/>.
    /// </summary>
    public const string ReloadHandshakeFailed = UrnPrefix + "python-reload-handshake-failed";

    /// <summary>
    /// A hot-reload was triggered for a plugin name that has no running supervisor —
    /// either the plugin was never loaded at startup or the folder was created after
    /// the host started. A silo restart is required to load new plugin folders.
    /// </summary>
    public const string ReloadNoSupervisor = UrnPrefix + "python-reload-no-supervisor";

    /// <summary>
    /// A hot-reload was triggered but the re-scan of <c>plugin.yaml</c> /
    /// <c>pyproject.toml</c> failed (file unreadable, parse error, ABI mismatch, etc.).
    /// The running subprocess is unaffected.
    /// </summary>
    public const string ReloadScanFailed = UrnPrefix + "python-reload-scan-failed";
}
