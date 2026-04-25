// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Runtime.Plugins.Python;

/// <summary>
/// Reloads a single Python plugin subprocess at runtime without restarting the host.
/// Invoked by <see cref="PythonPluginWatcherService"/> on filesystem change, or
/// directly by operators / tests.
/// The subprocess is replaced in-place: existing <c>PythonAgentShim</c> instances
/// continue to hold the same supervisor reference and pick up the new process
/// transparently on the next <c>AskAsync</c> call.
/// </summary>
internal interface IPythonPluginReloader
{
    /// <summary>
    /// Reload the Python plugin whose root directory is <paramref name="pluginDirectory"/>.
    /// Re-reads <c>plugin.yaml</c> + <c>pyproject.toml</c>, drains in-flight invokes,
    /// kills the old subprocess, and starts a new one. Returns the reload outcome.
    /// On failure the old subprocess is unaffected (or already stopped before the new
    /// one failed to start, in which case the supervisor transitions to Unavailable).
    /// </summary>
    Task<PythonPluginReloadResult> ReloadAsync(
        string pluginDirectory,
        CancellationToken ct = default);
}

/// <summary>
/// Outcome of a single <see cref="IPythonPluginReloader.ReloadAsync"/> call.
/// </summary>
/// <param name="PluginName">Friendly name derived from the plugin folder.</param>
/// <param name="Status">Whether the reload succeeded or why it was refused / failed.</param>
/// <param name="FailureUrn">URN from <see cref="PythonPluginUrns"/> on non-success outcomes.</param>
/// <param name="FailureException">Underlying exception, if any.</param>
public sealed record PythonPluginReloadResult(
    string PluginName,
    PythonPluginReloadStatus Status,
    string? FailureUrn,
    Exception? FailureException);

/// <summary>Outcome categories for a Python plugin hot-reload attempt.</summary>
public enum PythonPluginReloadStatus
{
    /// <summary>New subprocess started and handshake succeeded.</summary>
    Success = 0,

    /// <summary>
    /// The new <c>plugin.yaml</c> carries a different <c>handler.typeName</c>;
    /// in-place reload is not supported. Silo restart required.
    /// </summary>
    HandlerTypeNameChanged = 1,

    /// <summary>
    /// <c>plugin.yaml</c> or <c>pyproject.toml</c> could not be re-parsed. Old subprocess unaffected.
    /// </summary>
    ScanFailed = 2,

    /// <summary>New subprocess started but the MCP handshake failed. Plugin is now Unavailable.</summary>
    HandshakeFailed = 3,

    /// <summary>
    /// No running supervisor found for this plugin name. Either the plugin was not loaded at
    /// startup or the folder appeared after the host started. Silo restart required.
    /// </summary>
    NoSupervisor = 4,
}
