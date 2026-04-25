// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Vais.Agents.Control;

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
    private readonly ISecretResolver? _secretResolver;

    internal DefaultPythonPluginReloader(
        PythonPluginHostService host,
        PythonPluginLoaderOptions options,
        TimeSpan drainTimeout,
        ILoggerFactory? loggerFactory = null,
        ISecretResolver? secretResolver = null)
    {
        _host = host;
        _options = options;
        _drainTimeout = drainTimeout;
        _logger = (loggerFactory ?? NullLoggerFactory.Instance)
            .CreateLogger<DefaultPythonPluginReloader>();
        _secretResolver = secretResolver;
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

        // Resolve secrets declared in the updated plugin.yaml.
        newDescriptor = await ResolveSecretsAsync(newDescriptor, ct).ConfigureAwait(false);
        if (newDescriptor is null)
        {
            return Failure(pluginName, PythonPluginReloadStatus.ScanFailed,
                PythonPluginUrns.SecretResolutionFailed, null);
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

    private async ValueTask<PythonPluginDescriptor?> ResolveSecretsAsync(
        PythonPluginDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        if (descriptor.SecretDeclarations.Count == 0)
            return descriptor;

        if (_secretResolver is null)
        {
            _logger.LogWarning(
                "[{Urn}] python-plugin-reload: plugin '{Name}' declares {Count} secret(s) " +
                "but no ISecretResolver is registered — reload aborted.",
                PythonPluginUrns.SecretResolutionFailed, descriptor.Name,
                descriptor.SecretDeclarations.Count);
            return null;
        }

        var secretRefs = new Dictionary<string, string>(
            descriptor.SecretDeclarations.Count, StringComparer.Ordinal);

        foreach (var (refName, secretUri) in descriptor.SecretDeclarations)
        {
            string value;
            try
            {
                value = await _secretResolver.ResolveAsync(secretUri, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is SecretNotFoundException or NotSupportedException)
            {
                _logger.LogWarning(ex,
                    "[{Urn}] python-plugin-reload: plugin '{Name}' cannot resolve secret " +
                    "'{RefName}' from '{Uri}' — reload aborted.",
                    PythonPluginUrns.SecretResolutionFailed, descriptor.Name, refName, secretUri);
                return null;
            }

            secretRefs[$"VAIS_SECRET_{refName}"] = value;
        }

        _logger.LogDebug(
            "python-plugin-reload: resolved {Count} secret(s) for plugin '{Name}'.",
            secretRefs.Count, descriptor.Name);

        return descriptor with { SecretRefs = secretRefs };
    }
}
