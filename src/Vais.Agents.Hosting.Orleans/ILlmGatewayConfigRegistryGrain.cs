// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Hosting.Orleans;

/// <summary>
/// Orleans grain that owns the persisted manifests for a single LLM gateway config id.
/// Primary key = <c>configId</c>; state is a version → manifest map.
/// </summary>
public interface ILlmGatewayConfigRegistryGrain : IGrainWithStringKey
{
    /// <summary>Persist the manifest under its version. Manifest crosses the grain boundary as a JSON string.</summary>
    ValueTask RegisterAsync(string manifestJson);

    /// <summary>Retrieve a manifest's JSON. <paramref name="version"/> null ⇒ latest registered version. Null on miss.</summary>
    ValueTask<string?> GetAsync(string? version);

    /// <summary>Drop the manifest for <paramref name="version"/>. No-op when not found (idempotent).</summary>
    ValueTask RemoveAsync(string version);

    /// <summary>List every stored manifest's JSON for this config id.</summary>
    ValueTask<IReadOnlyList<string>> ListAsync();
}

/// <summary>
/// Singleton directory grain tracking the set of LLM gateway config ids registered in the cluster.
/// Used by <c>OrleansLlmGatewayConfigRegistry.ListAsync</c>.
/// </summary>
public interface ILlmGatewayConfigRegistryDirectoryGrain : IGrainWithStringKey
{
    /// <summary>Add <paramref name="configId"/> to the tracked set. Idempotent.</summary>
    ValueTask TrackAsync(string configId);

    /// <summary>Remove <paramref name="configId"/> from the tracked set. Idempotent.</summary>
    ValueTask UntrackAsync(string configId);

    /// <summary>List every tracked config id.</summary>
    ValueTask<IReadOnlyList<string>> ListIdsAsync();
}
