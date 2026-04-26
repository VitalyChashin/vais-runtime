// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Vais.Agents;
using Vais.Agents.Runtime.Plugins;

namespace Vais.Agents.Runtime.Host;

/// <summary>
/// Startup consistency check: walks every registered <see cref="AgentManifest"/> and
/// verifies that manifests with a <c>Handler.TypeName</c> resolve to a loaded plugin
/// handler. Logs <see cref="LogLevel.Error"/> (does not throw) for each miss so that
/// mis-deployed plugins surface at host start rather than at first invocation.
/// </summary>
/// <remarks>
/// Runs once on <see cref="StartAsync"/>. No-ops when no plugin registry is registered
/// (i.e., when both .NET and Python plugin loading are disabled).
/// </remarks>
internal sealed class PluginManifestConsistencyCheck : IHostedService
{
    private readonly IAgentRegistry _registry;
    private readonly IPluginHandlerRegistry? _pluginRegistry;
    private readonly ILogger<PluginManifestConsistencyCheck> _logger;

    public PluginManifestConsistencyCheck(
        IAgentRegistry registry,
        IServiceProvider sp,
        ILogger<PluginManifestConsistencyCheck> logger)
    {
        _registry = registry;
        _pluginRegistry = sp.GetService(typeof(IPluginHandlerRegistry)) as IPluginHandlerRegistry;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_pluginRegistry is null)
            return;

        await foreach (var manifest in _registry.ListAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            var typeName = manifest.Handler?.TypeName;
            if (string.IsNullOrWhiteSpace(typeName))
                continue;

            if (!_pluginRegistry.TryGet(typeName, out _))
            {
                _logger.LogError(
                    "Agent '{AgentId}' (version '{Version}') declares handler '{TypeName}' " +
                    "but no matching plugin is loaded. Registered handlers: [{Registered}]. " +
                    "Ensure the plugin DLL is present in the plugins directory and targets the correct ABI.",
                    manifest.Id,
                    manifest.Version,
                    typeName,
                    string.Join(", ", _pluginRegistry.HandlerTypeNames));
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
