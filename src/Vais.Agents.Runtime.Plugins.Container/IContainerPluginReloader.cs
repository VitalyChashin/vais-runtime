// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Runtime.Plugins.Container;

/// <summary>
/// Drains and replaces a running container plugin with a new image.
/// Used by <c>POST /v1/plugins/{name}/image</c>.
/// </summary>
public interface IContainerPluginReloader
{
    /// <summary>
    /// Replaces the container for <paramref name="pluginName"/> with <paramref name="newImage"/>.
    /// Drains in-flight invokes first, then stops, updates, and restarts the container.
    /// </summary>
    Task<ContainerPluginReloadResult> ReloadAsync(
        string pluginName,
        string newImage,
        CancellationToken ct = default);
}

/// <summary>Outcome of a container plugin image reload.</summary>
public enum ContainerPluginReloadStatus
{
    /// <summary>Container replaced and health check passed.</summary>
    Success = 0,
    /// <summary>Container started but health check timed out or failed.</summary>
    HandshakeFailed = 1,
    /// <summary>Container could not be started (Docker API error).</summary>
    StartFailed = 2,
    /// <summary>The new image declares a different handler type name. Silo restart required.</summary>
    HandlerTypeNameChanged = 3,
    /// <summary>No supervisor is loaded for this plugin name.</summary>
    NoSupervisor = 4,
    /// <summary>Kubernetes deployment patched; rolling update started. Not an error.</summary>
    RolloutStarted = 5,
}

/// <summary>Result of a <see cref="IContainerPluginReloader.ReloadAsync"/> call.</summary>
public sealed record ContainerPluginReloadResult(
    string PluginName,
    ContainerPluginReloadStatus Status,
    string? FailureUrn,
    Exception? FailureException = null);
