// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Control;

/// <summary>
/// Thrown when a <see cref="ContainerPluginHandle"/> references a plugin that is not
/// registered in the current <see cref="IContainerPluginRegistry"/>. HTTP layer maps to
/// 404 with URN <c>urn:vais-agents:container-plugin-handle-not-found</c>.
/// </summary>
public sealed class ContainerPluginHandleNotFoundException : Exception
{
    /// <summary>Plugin id that was not found.</summary>
    public string PluginId { get; }

    /// <summary>Version that was not found.</summary>
    public string Version { get; }

    /// <inheritdoc cref="ContainerPluginHandleNotFoundException"/>
    public ContainerPluginHandleNotFoundException(string pluginId, string version)
        : base($"ContainerPlugin '{pluginId}' version '{version}' is not registered.")
    {
        PluginId = pluginId;
        Version = version;
    }
}
