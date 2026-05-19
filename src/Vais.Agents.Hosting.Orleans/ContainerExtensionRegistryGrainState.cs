// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Hosting.Orleans;

/// <summary>Persisted state of <see cref="ContainerExtensionRegistryGrain"/>.</summary>
[GenerateSerializer]
public sealed class ContainerExtensionRegistryGrainState
{
    /// <summary>Stored manifest YAML payloads keyed by version string.</summary>
    [Id(0)]
    public Dictionary<string, string> ManifestYamlByVersion { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Version of the most recent registration call.</summary>
    [Id(1)]
    public string? LatestVersion { get; set; }
}

/// <summary>Persisted state of <see cref="ContainerExtensionRegistryDirectoryGrain"/>.</summary>
[GenerateSerializer]
public sealed class ContainerExtensionRegistryDirectoryGrainState
{
    /// <summary>Set of tracked extension ids.</summary>
    [Id(0)]
    public HashSet<string> ExtensionIds { get; set; } = new(StringComparer.Ordinal);
}
