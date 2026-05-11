// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Control;

/// <summary>
/// Thrown when a <see cref="ContainerPluginHandle"/> id+version already exists in the
/// registry. HTTP layer maps to 409 with URN <c>urn:vais-agents:container-plugin-already-exists</c>.
/// </summary>
public sealed class ContainerPluginConflictException : Exception
{
    /// <summary>Plugin id that already exists.</summary>
    public string PluginId { get; }

    /// <summary>Version that already exists.</summary>
    public string Version { get; }

    /// <inheritdoc cref="ContainerPluginConflictException"/>
    public ContainerPluginConflictException(string pluginId, string version)
        : base($"ContainerPlugin '{pluginId}' version '{version}' is already registered.")
    {
        PluginId = pluginId;
        Version = version;
    }
}
