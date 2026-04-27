// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Vais.Agents.Hosting.Orleans;

/// <summary>
/// <see cref="ILlmGatewayConfigRegistry"/> backed by per-id grains with a directory
/// grain maintaining the enumerable index. Registrations survive silo restart via the
/// configured Orleans grain-storage provider.
/// </summary>
public sealed class OrleansLlmGatewayConfigRegistry : ILlmGatewayConfigRegistry
{
    /// <summary>Primary-key string for the cluster's single <see cref="ILlmGatewayConfigRegistryDirectoryGrain"/>.</summary>
    public const string DirectoryKey = "vais.agents.llm-gateway-config-registry.directory";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IGrainFactory _grainFactory;

    /// <summary>DI ctor.</summary>
    public OrleansLlmGatewayConfigRegistry(IGrainFactory grainFactory)
    {
        ArgumentNullException.ThrowIfNull(grainFactory);
        _grainFactory = grainFactory;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<LlmGatewayConfigManifest> ListAsync(
        string? labelPrefix = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var directory = _grainFactory.GetGrain<ILlmGatewayConfigRegistryDirectoryGrain>(DirectoryKey);
        var ids = await directory.ListIdsAsync();

        foreach (var id in ids)
        {
            if (ct.IsCancellationRequested) yield break;

            var grain = _grainFactory.GetGrain<ILlmGatewayConfigRegistryGrain>(id);
            var manifests = await grain.ListAsync();
            foreach (var json in manifests)
            {
                var manifest = DeserializeManifest(json);
                if (labelPrefix is null || manifest.Labels?.Any(kv => kv.Key.StartsWith(labelPrefix, StringComparison.Ordinal)) == true)
                    yield return manifest;
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask<LlmGatewayConfigManifest?> GetAsync(
        string id, string? version = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ct.ThrowIfCancellationRequested();

        var grain = _grainFactory.GetGrain<ILlmGatewayConfigRegistryGrain>(id);
        var json = await grain.GetAsync(version);
        return json is null ? null : DeserializeManifest(json);
    }

    /// <inheritdoc />
    public async ValueTask RegisterAsync(LlmGatewayConfigManifest manifest, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ct.ThrowIfCancellationRequested();

        var grain = _grainFactory.GetGrain<ILlmGatewayConfigRegistryGrain>(manifest.Id);
        await grain.RegisterAsync(SerializeManifest(manifest));
    }

    /// <inheritdoc />
    public async ValueTask RemoveAsync(string id, string version, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ct.ThrowIfCancellationRequested();

        var grain = _grainFactory.GetGrain<ILlmGatewayConfigRegistryGrain>(id);
        await grain.RemoveAsync(version);
    }

    /// <summary>Serialise a manifest to JSON using the registry's settings.</summary>
    public static string SerializeManifest(LlmGatewayConfigManifest manifest) =>
        JsonSerializer.Serialize(manifest, SerializerOptions);

    /// <summary>Deserialise a manifest from JSON using the registry's settings.</summary>
    public static LlmGatewayConfigManifest DeserializeManifest(string json) =>
        JsonSerializer.Deserialize<LlmGatewayConfigManifest>(json, SerializerOptions)
            ?? throw new InvalidOperationException("LlmGatewayConfigManifest JSON deserialised to null.");
}
