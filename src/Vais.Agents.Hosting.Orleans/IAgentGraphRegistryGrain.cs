// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Hosting.Orleans;

/// <summary>
/// Orleans grain that owns the persisted manifests for a single graph id.
/// Primary key = <c>graphId</c>; state is a version → manifest map.
/// </summary>
public interface IAgentGraphRegistryGrain : IGrainWithStringKey
{
    /// <summary>
    /// Persist the manifest under its (<c>Id</c>, <c>Version</c>) pair. The
    /// manifest crosses the grain boundary as a JSON-serialised string. Returns
    /// the stored manifest's JSON (round-tripped, identical to input).
    /// </summary>
    ValueTask<string> RegisterAsync(string manifestJson);

    /// <summary>Retrieve a manifest's JSON. <paramref name="version"/> null ⇒ latest registered version. Null return when no matching version exists.</summary>
    ValueTask<string?> GetAsync(string? version);

    /// <summary>Drop the manifest for <paramref name="version"/> (or all versions when null). Returns true if anything was removed.</summary>
    ValueTask<bool> RemoveAsync(string? version);

    /// <summary>List every stored manifest's JSON for this graph id.</summary>
    ValueTask<IReadOnlyList<string>> ListAsync();
}

/// <summary>
/// Well-known singleton grain tracking the set of graph ids registered in the
/// cluster. Used by <c>OrleansAgentGraphRegistry.ListAsync</c>.
/// </summary>
public interface IAgentGraphRegistryDirectoryGrain : IGrainWithStringKey
{
    /// <summary>Add <paramref name="graphId"/> to the tracked set. Idempotent.</summary>
    ValueTask TrackAsync(string graphId);

    /// <summary>Remove <paramref name="graphId"/> from the tracked set. Idempotent.</summary>
    ValueTask UntrackAsync(string graphId);

    /// <summary>List every tracked graph id.</summary>
    ValueTask<IReadOnlyList<string>> ListIdsAsync();
}
