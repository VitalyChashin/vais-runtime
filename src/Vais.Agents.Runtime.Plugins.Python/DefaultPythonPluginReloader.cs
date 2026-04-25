// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Vais.Agents.Runtime.Plugins.Python;

/// <summary>
/// Default implementation of <see cref="IPythonPluginReloader"/>.
/// Re-reads the descriptor from disk via <see cref="PythonPluginScanner"/>, then
/// delegates to <see cref="PythonSubprocessSupervisor.DrainAndRestartAsync"/>.
/// Registered in DI when <see cref="PythonPluginLoaderOptions.ReloadPolicy"/> is
/// <see cref="ReloadPolicy.DrainAndSwap"/>.
/// </summary>
internal sealed class DefaultPythonPluginReloader : IPythonPluginReloader
{
    private readonly PythonPluginHostService _host;
    private readonly PythonPluginLoaderOptions _options;
    private readonly TimeSpan _drainTimeout;
    private readonly ILogger<DefaultPythonPluginReloader> _logger;

    internal DefaultPythonPluginReloader(
        PythonPluginHostService host,
        PythonPluginLoaderOptions options,
        TimeSpan drainTimeout,
        ILoggerFactory? loggerFactory = null)
    {
        _host = host;
        _options = options;
        _drainTimeout = drainTimeout;
        _logger = (loggerFactory ?? NullLoggerFactory.Instance)
            .CreateLogger<DefaultPythonPluginReloader>();
    }

    /// <inheritdoc />
    public async Task<PythonPluginReloadResult> ReloadAsync(
        string pluginDirectory,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginDirectory);

        var pluginName = Path.GetFileName(pluginDirectory.TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        _logger.LogInformation(
            "python-plugin-reload: scanning '{Dir}' for updated descriptor.", pluginDirectory);

        // Re-read descriptor from disk.  On failure the old subprocess is untouched.
        var scanner = new PythonPluginScanner(
            _options,
            NullLogger<PythonPluginScanner>.Instance);

        PythonPluginDescriptor? newDescriptor;
        try
        {
            newDescriptor = scanner.TryLoadDescriptor(pluginDirectory);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "[{Urn}] python-plugin-reload: descriptor scan threw for '{Dir}'.",
                PythonPluginUrns.ReloadScanFailed, pluginDirectory);
            return Failure(pluginName, PythonPluginReloadStatus.ScanFailed,
                PythonPluginUrns.ReloadScanFailed, ex);
        }

        if (newDescriptor is null)
        {
            _logger.LogWarning(
                "[{Urn}] python-plugin-reload: no valid descriptor produced from '{Dir}' — " +
                "plugin.yaml may be invalid or ABI mismatched. Running subprocess unaffected.",
                PythonPluginUrns.ReloadScanFailed, pluginDirectory);
            return Failure(pluginName, PythonPluginReloadStatus.ScanFailed,
                PythonPluginUrns.ReloadScanFailed, null);
        }

        // Look up the running supervisor.
        if (!_host.TryGetSupervisor(newDescriptor.Name, out var supervisor))
        {
            _logger.LogWarning(
                "[{Urn}] python-plugin-reload: no supervisor found for plugin '{Name}'. " +
                "New plugin folders require a silo restart.",
                PythonPluginUrns.ReloadNoSupervisor, newDescriptor.Name);
            return Failure(newDescriptor.Name, PythonPluginReloadStatus.NoSupervisor,
                PythonPluginUrns.ReloadNoSupervisor, null);
        }

        // Drain + kill + respawn.
        bool ok = await supervisor.DrainAndRestartAsync(newDescriptor, _drainTimeout, ct)
            .ConfigureAwait(false);

        if (!ok)
        {
            // DrainAndRestartAsync returns false for two reasons:
            //   1. HandlerTypeName changed (supervisor is unaffected).
            //   2. Handshake failed (supervisor is now Unavailable).
            // Distinguish by checking the descriptor's HandlerTypeName.
            bool typeNameChanged = !string.Equals(
                newDescriptor.HandlerTypeName, supervisor.Descriptor.HandlerTypeName,
                StringComparison.Ordinal);

            return typeNameChanged
                ? Failure(newDescriptor.Name, PythonPluginReloadStatus.HandlerTypeNameChanged,
                    PythonPluginUrns.ReloadHandlerTypeNameChanged, null)
                : Failure(newDescriptor.Name, PythonPluginReloadStatus.HandshakeFailed,
                    PythonPluginUrns.ReloadHandshakeFailed, null);
        }

        return new PythonPluginReloadResult(newDescriptor.Name, PythonPluginReloadStatus.Success,
            null, null);
    }

    private static PythonPluginReloadResult Failure(
        string name,
        PythonPluginReloadStatus status,
        string urn,
        Exception? ex) =>
        new(name, status, urn, ex);
}
