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
}
