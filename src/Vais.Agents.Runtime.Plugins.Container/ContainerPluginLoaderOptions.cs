// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Runtime.Plugins.Container;

/// <summary>Options for <see cref="ContainerPluginServiceCollectionExtensions.AddContainerPlugins"/>.</summary>
public sealed class ContainerPluginLoaderOptions
{
    /// <summary>Root directory scanned for plugin subfolders containing <c>plugin.yaml</c>.</summary>
    public string PluginsDirectory { get; set; } = "plugins";

    /// <summary>Base URL of the internal gateway (e.g. <c>http://localhost:5001</c>).</summary>
    public string InternalGatewayBaseUrl { get; set; } = "http://localhost:5001";

    /// <summary>Minimum supported container API version (inclusive).</summary>
    public string SupportedApiVersionMin { get; set; } = "0.24";

    /// <summary>Maximum supported container API version (inclusive).</summary>
    public string SupportedApiVersionMax { get; set; } = "0.24";

    /// <summary>Operator-configured upper bounds for per-plugin resource requests.</summary>
    public ContainerPluginResourceBounds ResourceBounds { get; set; } = new();
}

/// <summary>
/// Upper bounds applied to resource requests in <c>spec.resources</c> of a plugin manifest.
/// Operators can raise or lower these to match cluster capacity.
/// </summary>
public sealed class ContainerPluginResourceBounds
{
    /// <summary>Maximum memory a plugin may request. Default 2 GiB.</summary>
    public long MaxMemoryBytes { get; init; } = 2L * 1024 * 1024 * 1024;

    /// <summary>Maximum CPU a plugin may request, in nanoCPUs (1 CPU = 1_000_000_000). Default 4 vCPU.</summary>
    public long MaxNanoCpus    { get; init; } = 4_000_000_000L;

    /// <summary>Maximum PID limit a plugin may request. Default 1024.</summary>
    public long MaxPidsLimit   { get; init; } = 1024;
}
