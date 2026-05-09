// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Runtime.Plugins.Container;

/// <summary>
/// Supervises the full lifecycle of all container plugin instances in the current silo.
/// Registered as a singleton <see cref="Microsoft.Extensions.Hosting.IHostedService"/>
/// via <c>AddContainerPlugins</c>.
/// </summary>
public interface IContainerPluginHost
{
    /// <summary>
    /// A snapshot of every container plugin the host attempted to load,
    /// together with its current lifecycle status.
    /// </summary>
    IReadOnlyList<LoadedContainerPlugin> LoadedPlugins { get; }
}

/// <summary>
/// Describes a single container plugin that has been loaded (or attempted) by
/// <see cref="IContainerPluginHost"/>.
/// </summary>
/// <param name="Name">Plugin name (from <c>plugin.yaml</c> metadata).</param>
/// <param name="Image">Container image reference.</param>
/// <param name="HandlerTypeName">Handler type name discovered from <c>/v1/metadata</c>.</param>
/// <param name="TargetApiVersion">API version declared by the plugin.</param>
/// <param name="Status">Current lifecycle state.</param>
public sealed record LoadedContainerPlugin(
    string Name,
    string Image,
    string HandlerTypeName,
    string TargetApiVersion,
    ContainerPluginStatus Status)
{
    /// <summary>Deployment topology: "standalone", "sidecar", or "kubernetes".</summary>
    public string Topology { get; init; } = "standalone";

    /// <summary>Kubernetes Deployment name (kubernetes topology only).</summary>
    public string? KubernetesDeploymentName { get; init; }

    /// <summary>Kubernetes namespace (kubernetes topology only).</summary>
    public string? KubernetesNamespace { get; init; }
}

/// <summary>Lifecycle states for a supervised container plugin.</summary>
public enum ContainerPluginStatus
{
    /// <summary>The container is being created and started.</summary>
    Created = 0,

    /// <summary>The container is starting; health check in progress.</summary>
    Starting = 1,

    /// <summary>The container is running and health check passed.</summary>
    Ready = 2,

    /// <summary>The container is being stopped.</summary>
    Stopping = 3,

    /// <summary>The container has stopped.</summary>
    Stopped = 4,

    /// <summary>The container failed to start or health check timed out.</summary>
    Failed = 5,
}
