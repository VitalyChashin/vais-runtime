// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.Extensions.Logging;

namespace Vais.Agents.Runtime.Plugins.Container;

internal sealed class DefaultContainerPluginReloader(
    ContainerPluginHostService host,
    ILogger<DefaultContainerPluginReloader>? logger = null)
    : IContainerPluginReloader
{
    public async Task<ContainerPluginReloadResult> ReloadAsync(
        string pluginName, string newImage, CancellationToken ct)
    {
        if (!host.TryGetSupervisor(pluginName, out var supervisor))
            return new(pluginName, ContainerPluginReloadStatus.NoSupervisor, ContainerPluginUrns.NoSupervisor);

        var result = await supervisor!.DrainAndReplaceAsync(newImage, ct).ConfigureAwait(false);

        var status = result.Outcome switch
        {
            ContainerReplaceOutcome.Success              => ContainerPluginReloadStatus.Success,
            ContainerReplaceOutcome.StartFailed          => ContainerPluginReloadStatus.StartFailed,
            ContainerReplaceOutcome.HandshakeFailed      => ContainerPluginReloadStatus.HandshakeFailed,
            ContainerReplaceOutcome.HandlerTypeNameChanged => ContainerPluginReloadStatus.HandlerTypeNameChanged,
            _                                            => ContainerPluginReloadStatus.HandshakeFailed,
        };

        var urn = result.Outcome switch
        {
            ContainerReplaceOutcome.Success                => (string?)null,
            ContainerReplaceOutcome.StartFailed            => ContainerPluginUrns.StartFailed,
            ContainerReplaceOutcome.HandlerTypeNameChanged => ContainerPluginUrns.HandlerTypeNameChanged,
            _                                              => ContainerPluginUrns.HealthCheckFailed,
        };

        if (status == ContainerPluginReloadStatus.Success)
            logger?.LogInformation("container-reload-success plugin={PluginName} image={Image}", pluginName, newImage);
        else
            logger?.LogWarning(
                "container-reload-failed plugin={PluginName} image={Image} outcome={Outcome}",
                pluginName, newImage, result.Outcome);

        return new(pluginName, status, urn);
    }
}
