// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Vais.Agents.Hosting.Orleans;

/// <summary>
/// <see cref="IAgentRegistry"/> backed by per-id grains with a directory grain
/// maintaining the enumerable index. Registrations survive silo restart via
/// the configured Orleans grain-storage provider.
/// </summary>
/// <remarks>
/// <para>
/// The implementation exposes mutation helpers (<see cref="RegisterAsync"/> /
/// <see cref="RemoveAsync"/>) that <see cref="IAgentRegistry"/> itself does
/// not carry — the contract is intentionally read-only, and callers who want
/// to mutate cast to this concrete type. Matches the
/// <c>InMemoryAgentRegistry.Register</c> / <c>Remove</c> pattern in Core.
/// </para>
/// <para>
/// Manifests cross the grain boundary as JSON strings (see
/// <see cref="IAgentRegistryGrain"/>). This registry handles the serialisation
/// at the service boundary so callers still work with <see cref="AgentManifest"/>
/// records.
/// </para>
/// </remarks>
public sealed class OrleansAgentRegistry : IAgentRegistry
{
    /// <summary>Primary-key string for the cluster's single <see cref="IAgentRegistryDirectoryGrain"/>.</summary>
    public const string DirectoryKey = "vais.agents.registry.directory";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IGrainFactory _grainFactory;

    /// <summary>DI ctor.</summary>
    public OrleansAgentRegistry(IGrainFactory grainFactory)
    {
        ArgumentNullException.ThrowIfNull(grainFactory);
        _grainFactory = grainFactory;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<AgentManifest> ListAsync(
        string? labelPrefix = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var directory = _grainFactory.GetGrain<IAgentRegistryDirectoryGrain>(DirectoryKey);
        var ids = await directory.ListIdsAsync().ConfigureAwait(false);

        foreach (var id in ids)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                yield break;
            }

            var grain = _grainFactory.GetGrain<IAgentRegistryGrain>(id);
            var manifestsJson = await grain.ListAsync().ConfigureAwait(false);
            foreach (var json in manifestsJson)
            {
                var manifest = DeserializeManifest(json);

                if (labelPrefix is null)
                {
                    yield return manifest;
                    continue;
                }

                if (manifest.Labels is null)
                {
                    continue;
                }

                if (manifest.Labels.Any(kv => kv.Key.StartsWith(labelPrefix, StringComparison.Ordinal)))
                {
                    yield return manifest;
                }
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask<AgentManifest?> GetAsync(
        string id,
        string? version = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        cancellationToken.ThrowIfCancellationRequested();

        var grain = _grainFactory.GetGrain<IAgentRegistryGrain>(id);
        var json = await grain.GetAsync(version).ConfigureAwait(false);
        return json is null ? null : DeserializeManifest(json);
    }

    /// <summary>Persist a manifest. Matches the shape of <c>InMemoryAgentRegistry.Register</c>.</summary>
    public async ValueTask<AgentManifest> RegisterAsync(AgentManifest manifest, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        cancellationToken.ThrowIfCancellationRequested();

        var grain = _grainFactory.GetGrain<IAgentRegistryGrain>(manifest.Id);
        var json = SerializeManifest(manifest);
        var echoed = await grain.RegisterAsync(json).ConfigureAwait(false);
        return DeserializeManifest(echoed);
    }

    /// <summary>
    /// Synchronous sibling to <see cref="RegisterAsync"/>, named to match the
    /// duck-typed shape <c>AgentLifecycleManager.RegisterManifest</c> looks for
    /// (<c>InMemoryAgentRegistry.Register(AgentManifest)</c>). The call is an
    /// in-process grain RPC (sub-millisecond); sync-over-async is acceptable
    /// for the apply / create path.
    /// </summary>
    // TODO: duck-typed callers (AgentLifecycleManager) should be ported to RegisterAsync so
    // this sync-over-async bridge can be removed. Safe today because callers run off HTTP threads,
    // not inside grain activations.
    public AgentManifest Register(AgentManifest manifest) =>
        RegisterAsync(manifest, CancellationToken.None).AsTask().GetAwaiter().GetResult();

    /// <summary>Delete a manifest (or every version of an agent). Matches <c>InMemoryAgentRegistry.Remove</c>.</summary>
    public ValueTask<bool> RemoveAsync(string id, string? version = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        cancellationToken.ThrowIfCancellationRequested();

        var grain = _grainFactory.GetGrain<IAgentRegistryGrain>(id);
        return grain.RemoveAsync(version);
    }

    /// <summary>
    /// Synchronous sibling to <see cref="RemoveAsync"/> with the
    /// <c>Remove(string, string)</c> shape <c>AgentLifecycleManager.RemoveManifest</c>
    /// duck-types onto. The version arg is required (not optional) to match the
    /// exact reflection signature.
    /// </summary>
    // TODO: same as Register — port AgentLifecycleManager to RemoveAsync to eliminate this bridge.
    public bool Remove(string id, string version) =>
        RemoveAsync(id, version, CancellationToken.None).AsTask().GetAwaiter().GetResult();

    /// <summary>Serialise a manifest to JSON using the registry's settings.</summary>
    public static string SerializeManifest(AgentManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return JsonSerializer.Serialize(manifest, SerializerOptions);
    }

    /// <summary>Deserialise a manifest from JSON using the registry's settings.</summary>
    public static AgentManifest DeserializeManifest(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        return JsonSerializer.Deserialize<AgentManifest>(json, SerializerOptions)
            ?? throw new InvalidOperationException("AgentManifest JSON deserialised to null.");
    }
}
