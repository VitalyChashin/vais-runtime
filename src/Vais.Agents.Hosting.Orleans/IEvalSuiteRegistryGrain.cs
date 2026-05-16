// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Hosting.Orleans;

/// <summary>
/// Orleans grain that owns the persisted manifests for a single eval suite id.
/// Primary key = <c>suiteId</c>; state is a version → manifest map.
/// </summary>
public interface IEvalSuiteRegistryGrain : IGrainWithStringKey
{
    /// <summary>Persist the manifest under its version (upsert). Manifest crosses the grain boundary as a JSON string.</summary>
    ValueTask UpsertAsync(string manifestJson);

    /// <summary>Retrieve a manifest's JSON. <paramref name="version"/> null ⇒ latest registered version. Null on miss.</summary>
    ValueTask<string?> GetAsync(string? version);

    /// <summary>Drop the manifest for <paramref name="version"/>. No-op when not found (idempotent).</summary>
    ValueTask RemoveAsync(string version);

    /// <summary>List every stored manifest's JSON for this suite id.</summary>
    ValueTask<IReadOnlyList<string>> ListAsync();
}

/// <summary>
/// Singleton directory grain tracking the set of eval suite ids registered in the cluster.
/// Used by <c>OrleansEvalSuiteRegistry.ListAllAsync</c>.
/// </summary>
public interface IEvalSuiteRegistryDirectoryGrain : IGrainWithStringKey
{
    /// <summary>Add <paramref name="suiteId"/> to the tracked set. Idempotent.</summary>
    ValueTask TrackAsync(string suiteId);

    /// <summary>Remove <paramref name="suiteId"/> from the tracked set. Idempotent.</summary>
    ValueTask UntrackAsync(string suiteId);

    /// <summary>List every tracked suite id.</summary>
    ValueTask<IReadOnlyList<string>> ListIdsAsync();
}
