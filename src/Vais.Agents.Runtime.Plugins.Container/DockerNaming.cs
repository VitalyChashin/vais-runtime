// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Runtime.Plugins.Container;

/// <summary>
/// Single source of truth for Docker container names and invoke URLs.
/// Keeps <see cref="DockerContainerSupervisor"/> and <see cref="ContainerPluginHostService"/>
/// in sync so the container-DNS hostname (used in internal-network mode) always matches
/// the name Docker assigns to the container.
/// </summary>
internal static class DockerNaming
{
    internal static string ContainerName(string pluginName) => $"vais-plugin-{pluginName}";

    /// <summary>
    /// Returns the base URL the runtime uses to invoke the plugin.
    /// In internal-network mode (<paramref name="networkName"/> is set) the URL uses
    /// Docker's embedded DNS, which resolves the container name to its IP on the shared network.
    /// In legacy mode (<paramref name="networkName"/> is null/empty) the URL uses the
    /// host-published port on 127.0.0.1.
    /// </summary>
    internal static string InvokeUrl(string pluginName, int port, string? networkName) =>
        networkName is { Length: > 0 }
            ? $"http://{ContainerName(pluginName)}:{port}"
            : $"http://localhost:{port}";
}
