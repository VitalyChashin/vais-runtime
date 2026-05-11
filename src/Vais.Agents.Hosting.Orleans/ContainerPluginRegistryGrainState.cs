// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Hosting.Orleans;

/// <summary>Persisted state of <see cref="ContainerPluginRegistryGrain"/>.</summary>
[GenerateSerializer]
public sealed class ContainerPluginRegistryGrainState
{
    /// <summary>Stored manifest JSON payloads keyed by version string.</summary>
    [Id(0)]
    public Dictionary<string, string> ManifestJsonByVersion { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Version of the most recent <see cref="ContainerPluginRegistryGrain.RegisterAsync"/> call.</summary>
    [Id(1)]
    public string? LatestVersion { get; set; }
}

/// <summary>Persisted state of <see cref="ContainerPluginRegistryDirectoryGrain"/>.</summary>
[GenerateSerializer]
public sealed class ContainerPluginRegistryDirectoryGrainState
{
    /// <summary>Set of tracked plugin ids.</summary>
    [Id(0)]
    public HashSet<string> PluginIds { get; set; } = new(StringComparer.Ordinal);
}
