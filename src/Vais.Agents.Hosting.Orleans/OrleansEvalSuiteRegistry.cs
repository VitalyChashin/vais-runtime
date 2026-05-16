// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Vais.Agents.Hosting.Orleans;

/// <summary>
/// Eval suite registry backed by per-id grains with a directory grain maintaining
/// the enumerable index. Registrations survive silo restart via the configured
/// Orleans grain-storage provider.
/// </summary>
public sealed class OrleansEvalSuiteRegistry : IEvalSuiteRegistry
{
    /// <summary>Primary-key string for the cluster's single <see cref="IEvalSuiteRegistryDirectoryGrain"/>.</summary>
    public const string DirectoryKey = "vais.agents.eval-suite-registry.directory";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IGrainFactory _grainFactory;

    /// <summary>DI ctor.</summary>
    public OrleansEvalSuiteRegistry(IGrainFactory grainFactory)
    {
        ArgumentNullException.ThrowIfNull(grainFactory);
        _grainFactory = grainFactory;
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<EvalSuiteManifest> ListAsync(
        string? labelPrefix = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var directory = _grainFactory.GetGrain<IEvalSuiteRegistryDirectoryGrain>(DirectoryKey);
        var ids = await directory.ListIdsAsync();

        foreach (var id in ids)
        {
            if (ct.IsCancellationRequested) yield break;

            var grain = _grainFactory.GetGrain<IEvalSuiteRegistryGrain>(id);
            var manifests = await grain.ListAsync();
            foreach (var json in manifests)
            {
                var manifest = DeserializeManifest(json);
                if (labelPrefix is null || MatchesLabelPrefix(manifest, labelPrefix))
                    yield return manifest;
            }
        }
    }

    private static bool MatchesLabelPrefix(EvalSuiteManifest manifest, string prefix)
    {
        if (manifest.Labels is null) return false;
        foreach (var key in manifest.Labels.Keys)
            if (key.StartsWith(prefix, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>Retrieve a specific eval suite manifest by id and optional version.</summary>
    public async ValueTask<EvalSuiteManifest?> GetAsync(string id, string? version = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ct.ThrowIfCancellationRequested();

        var grain = _grainFactory.GetGrain<IEvalSuiteRegistryGrain>(id);
        var json = await grain.GetAsync(version);
        return json is null ? null : DeserializeManifest(json);
    }

    /// <summary>Upsert an eval suite manifest (overwrites existing same id+version).</summary>
    public async ValueTask UpsertAsync(EvalSuiteManifest manifest, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ct.ThrowIfCancellationRequested();

        var grain = _grainFactory.GetGrain<IEvalSuiteRegistryGrain>(manifest.Id);
        await grain.UpsertAsync(SerializeManifest(manifest));
    }

    /// <summary>Remove an eval suite manifest by id and version.</summary>
    public async ValueTask RemoveAsync(string id, string version, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ct.ThrowIfCancellationRequested();

        var grain = _grainFactory.GetGrain<IEvalSuiteRegistryGrain>(id);
        await grain.RemoveAsync(version);
    }

    /// <summary>Serialise a manifest to JSON using the registry's settings.</summary>
    public static string SerializeManifest(EvalSuiteManifest manifest) =>
        JsonSerializer.Serialize(manifest, SerializerOptions);

    /// <summary>Deserialise a manifest from JSON using the registry's settings.</summary>
    public static EvalSuiteManifest DeserializeManifest(string json) =>
        JsonSerializer.Deserialize<EvalSuiteManifest>(json, SerializerOptions)
            ?? throw new InvalidOperationException("EvalSuiteManifest JSON deserialised to null.");
}
