// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Hosting.Orleans;

/// <summary>Persisted state of <see cref="EvalSuiteRegistryGrain"/>.</summary>
[GenerateSerializer]
public sealed class EvalSuiteRegistryGrainState
{
    /// <summary>Stored manifest JSON payloads keyed by version string.</summary>
    [Id(0)]
    public Dictionary<string, string> ManifestJsonByVersion { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Version of the most recent <see cref="EvalSuiteRegistryGrain.UpsertAsync"/> call.</summary>
    [Id(1)]
    public string? LatestVersion { get; set; }
}

/// <summary>Persisted state of <see cref="EvalSuiteRegistryDirectoryGrain"/>.</summary>
[GenerateSerializer]
public sealed class EvalSuiteRegistryDirectoryGrainState
{
    /// <summary>Set of tracked suite ids.</summary>
    [Id(0)]
    public HashSet<string> SuiteIds { get; set; } = new(StringComparer.Ordinal);
}
