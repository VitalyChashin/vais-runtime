// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Control;

/// <summary>
/// Control-plane lifecycle manager for <see cref="IContainerPluginRegistry"/>-hosted
/// container plugin manifests. Validates, persists, and starts/stops the container
/// for each create/update/evict verb.
/// </summary>
public interface IContainerPluginLifecycleManager
{
    /// <summary>Register a new plugin manifest and start the container. Throws <see cref="ContainerPluginConflictException"/> on id+version collision (409).</summary>
    ValueTask<ContainerPluginHandle> CreateAsync(
        ContainerPluginManifest manifest, CancellationToken ct = default);

    /// <summary>Update an existing plugin manifest. Image changes trigger a drain-replace reload.</summary>
    ValueTask<ContainerPluginHandle> UpdateAsync(
        ContainerPluginHandle handle, ContainerPluginManifest newManifest, CancellationToken ct = default);

    /// <summary>Return current status for the plugin.</summary>
    /// <exception cref="ContainerPluginHandleNotFoundException">Handle was not found in the registry.</exception>
    ValueTask<ContainerPluginRuntimeStatus> QueryAsync(
        ContainerPluginHandle handle, CancellationToken ct = default);

    /// <summary>Enumerate all registered plugins, optionally filtered by a label prefix.</summary>
    IAsyncEnumerable<ContainerPluginManifest> ListAsync(
        string? labelPrefix = null, CancellationToken ct = default);

    /// <summary>Stop the container and remove the manifest from the registry.</summary>
    /// <exception cref="ContainerPluginHandleNotFoundException">Handle was not found in the registry.</exception>
    ValueTask EvictAsync(ContainerPluginHandle handle, CancellationToken ct = default);
}
