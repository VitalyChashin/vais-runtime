// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Hosting.Orleans;

/// <summary>Persisted state of <see cref="AgentRegistryGrain"/>.</summary>
[GenerateSerializer]
public sealed class AgentRegistryGrainState
{
    /// <summary>Stored manifest JSON payloads keyed by version string. Stored as JSON so <c>AgentManifest</c> (in Orleans-free <c>Abstractions</c>) doesn't need <c>[GenerateSerializer]</c>.</summary>
    [Id(0)]
    public Dictionary<string, string> ManifestJsonByVersion { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Version of the most recent <see cref="AgentRegistryGrain.RegisterAsync"/> call (used by <c>GetAsync(version: null)</c>).</summary>
    [Id(1)]
    public string? LatestVersion { get; set; }
}

/// <summary>Persisted state of <see cref="AgentRegistryDirectoryGrain"/>.</summary>
[GenerateSerializer]
public sealed class AgentRegistryDirectoryGrainState
{
    /// <summary>Set of tracked agent ids.</summary>
    [Id(0)]
    public HashSet<string> AgentIds { get; set; } = new(StringComparer.Ordinal);
}
