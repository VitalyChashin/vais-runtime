// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Hosting.Orleans;

/// <summary>
/// Orleans grain that owns the persisted manifest for a single container extension id.
/// Primary key = extension id; state is a version → manifest YAML map.
/// Parallel to <see cref="IContainerPluginRegistryGrain"/> for <c>kind: Extension</c>.
/// </summary>
public interface IContainerExtensionRegistryGrain : IGrainWithStringKey
{
    /// <summary>Persist the manifest YAML under its version.</summary>
    ValueTask RegisterAsync(string manifestYaml);

    /// <summary>Retrieve manifest YAML. <paramref name="version"/> null ⇒ latest. Null on miss.</summary>
    ValueTask<string?> GetAsync(string? version);

    /// <summary>Drop the manifest for <paramref name="version"/>. No-op when not found.</summary>
    ValueTask RemoveAsync(string version);

    /// <summary>List every stored manifest YAML for this extension id.</summary>
    ValueTask<IReadOnlyList<string>> ListAsync();
}

/// <summary>
/// Singleton directory grain tracking all registered container extension ids.
/// </summary>
public interface IContainerExtensionRegistryDirectoryGrain : IGrainWithStringKey
{
    /// <summary>Add <paramref name="extensionId"/> to the tracked set. Idempotent.</summary>
    ValueTask TrackAsync(string extensionId);

    /// <summary>Remove <paramref name="extensionId"/> from the tracked set. Idempotent.</summary>
    ValueTask UntrackAsync(string extensionId);

    /// <summary>List every tracked extension id.</summary>
    ValueTask<IReadOnlyList<string>> ListIdsAsync();
}
