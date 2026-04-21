// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Vais.Agents.Hosting.Orleans;

/// <summary>
/// <see cref="IAgentGraphRegistry"/> backed by per-id grains with a directory
/// grain maintaining the enumerable index. Registrations survive silo restart
/// via the configured Orleans grain-storage provider.
/// </summary>
/// <remarks>
/// <para>
/// The implementation exposes mutation helpers (<see cref="RegisterAsync"/> /
/// <see cref="RemoveAsync"/>) that <see cref="IAgentGraphRegistry"/> itself does
/// not carry — the contract is intentionally read-only, and callers who want to
/// mutate cast to this concrete type. Matches the
/// <c>InMemoryAgentGraphRegistry.Register</c> / <c>Remove</c> pattern in Core.
/// </para>
/// <para>
/// Manifests cross the grain boundary as JSON strings. This registry handles
/// serialisation at the service boundary so callers still work with
/// <see cref="AgentGraphManifest"/> records.
/// </para>
/// </remarks>
public sealed class OrleansAgentGraphRegistry : IAgentGraphRegistry
{
    /// <summary>Primary-key string for the cluster's single <see cref="IAgentGraphRegistryDirectoryGrain"/>.</summary>
    public const string DirectoryKey = "vais.agents.graph-registry.directory";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IGrainFactory _grainFactory;

    /// <summary>DI ctor.</summary>
    public OrleansAgentGraphRegistry(IGrainFactory grainFactory)
    {
        ArgumentNullException.ThrowIfNull(grainFactory);
        _grainFactory = grainFactory;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<AgentGraphManifest> ListAsync(
        string? labelPrefix = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var directory = _grainFactory.GetGrain<IAgentGraphRegistryDirectoryGrain>(DirectoryKey);
        var ids = await directory.ListIdsAsync();

        foreach (var id in ids)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                yield break;
            }

            var grain = _grainFactory.GetGrain<IAgentGraphRegistryGrain>(id);
            var manifestsJson = await grain.ListAsync();
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
    public async ValueTask<AgentGraphManifest?> GetAsync(
        string id,
        string? version = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        cancellationToken.ThrowIfCancellationRequested();

        var grain = _grainFactory.GetGrain<IAgentGraphRegistryGrain>(id);
        var json = await grain.GetAsync(version);
        return json is null ? null : DeserializeManifest(json);
    }

    /// <summary>Persist a manifest. Matches the shape of <c>InMemoryAgentGraphRegistry.Register</c>.</summary>
    public async ValueTask<AgentGraphManifest> RegisterAsync(AgentGraphManifest manifest, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        cancellationToken.ThrowIfCancellationRequested();

        var grain = _grainFactory.GetGrain<IAgentGraphRegistryGrain>(manifest.Id);
        var json = SerializeManifest(manifest);
        var echoed = await grain.RegisterAsync(json);
        return DeserializeManifest(echoed);
    }

    /// <summary>
    /// Synchronous sibling to <see cref="RegisterAsync"/>, named to match the
    /// duck-typed shape <c>AgentGraphLifecycleManager</c> looks for
    /// (<c>InMemoryAgentGraphRegistry.Register(AgentGraphManifest)</c>).
    /// </summary>
    public AgentGraphManifest Register(AgentGraphManifest manifest) =>
        RegisterAsync(manifest, CancellationToken.None).AsTask().GetAwaiter().GetResult();

    /// <summary>Delete a manifest (or every version of a graph). Matches <c>InMemoryAgentGraphRegistry.Remove</c>.</summary>
    public ValueTask<bool> RemoveAsync(string id, string? version = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        cancellationToken.ThrowIfCancellationRequested();

        var grain = _grainFactory.GetGrain<IAgentGraphRegistryGrain>(id);
        return grain.RemoveAsync(version);
    }

    /// <summary>
    /// Synchronous sibling to <see cref="RemoveAsync"/> with the
    /// <c>Remove(string, string)</c> shape that duck-typing callers look for.
    /// </summary>
    public bool Remove(string id, string version) =>
        RemoveAsync(id, version, CancellationToken.None).AsTask().GetAwaiter().GetResult();

    /// <summary>Serialise a manifest to JSON using the registry's settings.</summary>
    public static string SerializeManifest(AgentGraphManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return JsonSerializer.Serialize(manifest, SerializerOptions);
    }

    /// <summary>Deserialise a manifest from JSON using the registry's settings.</summary>
    public static AgentGraphManifest DeserializeManifest(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        return JsonSerializer.Deserialize<AgentGraphManifest>(json, SerializerOptions)
            ?? throw new InvalidOperationException("AgentGraphManifest JSON deserialised to null.");
    }
}
