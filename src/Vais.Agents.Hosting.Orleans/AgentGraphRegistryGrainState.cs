// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Hosting.Orleans;

/// <summary>Persisted state of <see cref="AgentGraphRegistryGrain"/>.</summary>
[GenerateSerializer]
public sealed class AgentGraphRegistryGrainState
{
    /// <summary>Stored manifest JSON payloads keyed by version string.</summary>
    [Id(0)]
    public Dictionary<string, string> ManifestJsonByVersion { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Version of the most recent <see cref="AgentGraphRegistryGrain.RegisterAsync"/> call.</summary>
    [Id(1)]
    public string? LatestVersion { get; set; }
}

/// <summary>Persisted state of <see cref="AgentGraphRegistryDirectoryGrain"/>.</summary>
[GenerateSerializer]
public sealed class AgentGraphRegistryDirectoryGrainState
{
    /// <summary>Set of tracked graph ids.</summary>
    [Id(0)]
    public HashSet<string> GraphIds { get; set; } = new(StringComparer.Ordinal);
}
