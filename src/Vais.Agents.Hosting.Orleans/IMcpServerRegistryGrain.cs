// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Hosting.Orleans;

/// <summary>
/// Orleans grain that owns the persisted manifests for a single MCP server id.
/// Primary key = <c>serverId</c>; state is a version → manifest map.
/// </summary>
public interface IMcpServerRegistryGrain : IGrainWithStringKey
{
    /// <summary>Persist the manifest under its version. Manifest crosses the grain boundary as a JSON string.</summary>
    ValueTask RegisterAsync(string manifestJson);

    /// <summary>Retrieve a manifest's JSON. <paramref name="version"/> null ⇒ latest registered version. Null on miss.</summary>
    ValueTask<string?> GetAsync(string? version);

    /// <summary>Drop the manifest for <paramref name="version"/>. No-op when not found (idempotent).</summary>
    ValueTask RemoveAsync(string version);

    /// <summary>List every stored manifest's JSON for this server id.</summary>
    ValueTask<IReadOnlyList<string>> ListAsync();
}

/// <summary>
/// Singleton directory grain tracking the set of MCP server ids registered in the cluster.
/// Used by <c>OrleansMcpServerRegistry.ListAsync</c>.
/// </summary>
public interface IMcpServerRegistryDirectoryGrain : IGrainWithStringKey
{
    /// <summary>Add <paramref name="serverId"/> to the tracked set. Idempotent.</summary>
    ValueTask TrackAsync(string serverId);

    /// <summary>Remove <paramref name="serverId"/> from the tracked set. Idempotent.</summary>
    ValueTask UntrackAsync(string serverId);

    /// <summary>List every tracked server id.</summary>
    ValueTask<IReadOnlyList<string>> ListIdsAsync();
}
