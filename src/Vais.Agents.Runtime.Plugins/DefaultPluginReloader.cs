// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Vais.Agents.Runtime.Plugins;

/// <summary>
/// Default implementation of <see cref="IPluginReloader"/>. Loads the new
/// plugin into a fresh <see cref="PluginAssemblyLoadContext"/>, atomically
/// swaps the handler registry via <see cref="PluginHandlerRegistry.SwapAsync"/>,
/// then invokes registered <see cref="IPluginReloadHook"/> observers.
/// Registered in DI when <see cref="ReloadPolicy.DrainAndSwap"/> is configured.
/// </summary>
internal sealed class DefaultPluginReloader : IPluginReloader
{
    private readonly AssemblyPluginLoader _loader;
    private readonly PluginHandlerRegistry _registry;
    private readonly IPluginReloadHook[] _hooks;
    private readonly ILogger<DefaultPluginReloader> _logger;

    internal DefaultPluginReloader(
        AssemblyPluginLoader loader,
        PluginHandlerRegistry registry,
        IEnumerable<IPluginReloadHook>? hooks = null,
        ILogger<DefaultPluginReloader>? logger = null)
    {
        _loader = loader;
        _registry = registry;
        _hooks = hooks?.ToArray() ?? [];
        _logger = logger ?? NullLogger<DefaultPluginReloader>.Instance;
    }

    /// <inheritdoc />
    public async Task<PluginReloadResult> ReloadAsync(
        string pluginPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginPath);

        var pluginFolder = Path.GetDirectoryName(pluginPath);
        if (string.IsNullOrEmpty(pluginFolder))
        {
            return Failure(null, PluginReloadUrns.PluginReloadFailed, null);
        }

        var pluginName = Path.GetFileName(pluginFolder);
        var oldDescriptor = _registry.Plugins.FirstOrDefault(p => p.Name == pluginName);

        _logger.LogInformation("reload-begin: plugin '{Plugin}' path={Path}", pluginName, pluginPath);

        var tempRegistry = new PluginHandlerRegistry();
        try
        {
            _loader.LoadPlugin(pluginFolder, tempRegistry);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "reload-failed: plugin '{Plugin}' could not be loaded — {Urn}",
                pluginName, PluginReloadUrns.PluginReloadFailed);
            return Failure(oldDescriptor, PluginReloadUrns.PluginReloadFailed, ex);
        }

        var newDescriptor = tempRegistry.Plugins.FirstOrDefault();
        if (newDescriptor is null)
        {
            // Loader already logged the specific reason (ABI mismatch, no handlers, etc.).
            _logger.LogWarning(
                "reload-failed: plugin '{Plugin}' produced no descriptor after load — {Urn}",
                pluginName, PluginReloadUrns.PluginReloadFailed);
            return Failure(oldDescriptor, PluginReloadUrns.PluginReloadFailed, null);
        }

        var swappedOld = await _registry.SwapAsync(
            pluginName,
            newDescriptor,
            tempRegistry.GetAllFactories(),
            cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "reload-success: plugin '{Plugin}' (handlers=[{Handlers}])",
            pluginName, string.Join(", ", newDescriptor.Handlers));

        var result = new PluginReloadResult(swappedOld, newDescriptor, PluginReloadStatus.Success, null, null);

        await DispatchHooksAsync(result, cancellationToken).ConfigureAwait(false);

        // Unload the old ALC after hooks complete so hooks can still access old types during drain.
        if (swappedOld?.LoadContext is { IsCollectible: true } oldAlc)
        {
            oldAlc.Unload();
        }

        return result;
    }

    private async Task DispatchHooksAsync(PluginReloadResult result, CancellationToken cancellationToken)
    {
        foreach (var hook in _hooks)
        {
            try
            {
                await hook.OnReloadedAsync(result, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Plugin reload hook '{Hook}' threw after swap — continuing.",
                    hook.GetType().Name);
            }
        }
    }

    private static PluginReloadResult Failure(
        PluginDescriptor? old,
        string failureUrn,
        Exception? ex) =>
        new(old, null, PluginReloadStatus.LoadFailed, failureUrn, ex);
}
