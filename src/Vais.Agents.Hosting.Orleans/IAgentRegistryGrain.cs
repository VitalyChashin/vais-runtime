// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Hosting.Orleans;

/// <summary>
/// Orleans grain that owns the persisted manifests for a single agent id.
/// Primary key = <c>agentId</c>; state is a version → manifest map so multiple
/// versions of the same logical agent can coexist (the v0.17 apply flow only
/// writes one version per manifest, but the registry contract allows more).
/// </summary>
public interface IAgentRegistryGrain : IGrainWithStringKey
{
    /// <summary>
    /// Persist the manifest under its (<c>Id</c>, <c>Version</c>) pair. The
    /// manifest crosses the grain boundary as a JSON-serialised string so
    /// <c>Vais.Agents.Abstractions</c> stays Orleans-free (records there have
    /// no <c>[GenerateSerializer]</c> attributes). Returns the stored
    /// manifest's JSON (round-tripped, identical to input in v0.17).
    /// </summary>
    ValueTask<string> RegisterAsync(string manifestJson);

    /// <summary>Retrieve a manifest's JSON. <paramref name="version"/> null ⇒ latest registered version. Null return when the grain has no matching version.</summary>
    ValueTask<string?> GetAsync(string? version);

    /// <summary>Drop the manifest for <paramref name="version"/> (or all versions when null). Returns true if anything was removed.</summary>
    ValueTask<bool> RemoveAsync(string? version);

    /// <summary>List every stored manifest's JSON for this agent id.</summary>
    ValueTask<IReadOnlyList<string>> ListAsync();
}

/// <summary>
/// Well-known singleton grain tracking the set of agent ids registered in the
/// cluster. Used by <c>OrleansAgentRegistry.ListAsync</c> — grain-per-id has
/// no built-in enumeration primitive so we maintain the index explicitly.
/// </summary>
public interface IAgentRegistryDirectoryGrain : IGrainWithStringKey
{
    /// <summary>Add <paramref name="agentId"/> to the tracked set. Idempotent.</summary>
    ValueTask TrackAsync(string agentId);

    /// <summary>Remove <paramref name="agentId"/> from the tracked set. Idempotent.</summary>
    ValueTask UntrackAsync(string agentId);

    /// <summary>List every tracked agent id.</summary>
    ValueTask<IReadOnlyList<string>> ListIdsAsync();
}
