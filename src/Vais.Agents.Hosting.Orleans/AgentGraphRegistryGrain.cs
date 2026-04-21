// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Hosting.Orleans;

/// <summary>
/// Per-graph-id registry grain. Primary key = graph id. Persists manifest JSON
/// payloads keyed by version under the <see cref="AiAgentGrain.StorageName"/>
/// provider, sharing the same grain-storage wiring as agent and checkpointer grains.
/// </summary>
public sealed class AgentGraphRegistryGrain : Grain, IAgentGraphRegistryGrain
{
    private readonly IPersistentState<AgentGraphRegistryGrainState> _state;
    private readonly IGrainFactory _grainFactory;

    /// <summary>DI ctor — grain-storage injection + cross-grain calls.</summary>
    public AgentGraphRegistryGrain(
        [PersistentState("graph-registry", AiAgentGrain.StorageName)] IPersistentState<AgentGraphRegistryGrainState> state,
        IGrainFactory grainFactory)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(grainFactory);
        _state = state;
        _grainFactory = grainFactory;
    }

    /// <inheritdoc />
    public async ValueTask<string> RegisterAsync(string manifestJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestJson);

        var manifest = OrleansAgentGraphRegistry.DeserializeManifest(manifestJson);

        var id = this.GetPrimaryKeyString();
        if (!string.Equals(id, manifest.Id, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Manifest id mismatch: grain key is '{id}' but manifest.Id is '{manifest.Id}'.");
        }

        _state.State.ManifestJsonByVersion[manifest.Version] = manifestJson;
        _state.State.LatestVersion = manifest.Version;
        await _state.WriteStateAsync();

        var directory = _grainFactory.GetGrain<IAgentGraphRegistryDirectoryGrain>(OrleansAgentGraphRegistry.DirectoryKey);
        await directory.TrackAsync(id);

        return manifestJson;
    }

    /// <inheritdoc />
    public ValueTask<string?> GetAsync(string? version)
    {
        if (_state.State.ManifestJsonByVersion.Count == 0)
        {
            return new ValueTask<string?>((string?)null);
        }

        if (version is null)
        {
            var latest = _state.State.LatestVersion;
            if (latest is null || !_state.State.ManifestJsonByVersion.TryGetValue(latest, out var json))
            {
                return new ValueTask<string?>((string?)null);
            }
            return new ValueTask<string?>(json);
        }

        return new ValueTask<string?>(
            _state.State.ManifestJsonByVersion.TryGetValue(version, out var versioned) ? versioned : null);
    }

    /// <inheritdoc />
    public async ValueTask<bool> RemoveAsync(string? version)
    {
        var removed = false;

        if (version is null)
        {
            removed = _state.State.ManifestJsonByVersion.Count > 0;
            _state.State.ManifestJsonByVersion.Clear();
            _state.State.LatestVersion = null;
        }
        else
        {
            removed = _state.State.ManifestJsonByVersion.Remove(version);
            if (string.Equals(_state.State.LatestVersion, version, StringComparison.Ordinal))
            {
                _state.State.LatestVersion = _state.State.ManifestJsonByVersion.Keys.FirstOrDefault();
            }
        }

        if (removed)
        {
            await _state.WriteStateAsync();
        }

        if (_state.State.ManifestJsonByVersion.Count == 0)
        {
            var id = this.GetPrimaryKeyString();
            var directory = _grainFactory.GetGrain<IAgentGraphRegistryDirectoryGrain>(OrleansAgentGraphRegistry.DirectoryKey);
            await directory.UntrackAsync(id);
        }

        return removed;
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<string>> ListAsync()
    {
        IReadOnlyList<string> snapshot = _state.State.ManifestJsonByVersion.Values.ToArray();
        return new ValueTask<IReadOnlyList<string>>(snapshot);
    }
}

/// <summary>
/// Singleton directory grain. One grain per cluster at the well-known key
/// <see cref="OrleansAgentGraphRegistry.DirectoryKey"/>. Maintains the set of
/// graph ids that have at least one registered manifest.
/// </summary>
public sealed class AgentGraphRegistryDirectoryGrain : Grain, IAgentGraphRegistryDirectoryGrain
{
    private readonly IPersistentState<AgentGraphRegistryDirectoryGrainState> _state;

    /// <summary>DI ctor — grain-storage injection.</summary>
    public AgentGraphRegistryDirectoryGrain(
        [PersistentState("graph-directory", AiAgentGrain.StorageName)] IPersistentState<AgentGraphRegistryDirectoryGrainState> state)
    {
        ArgumentNullException.ThrowIfNull(state);
        _state = state;
    }

    /// <inheritdoc />
    public async ValueTask TrackAsync(string graphId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(graphId);
        if (_state.State.GraphIds.Add(graphId))
        {
            await _state.WriteStateAsync();
        }
    }

    /// <inheritdoc />
    public async ValueTask UntrackAsync(string graphId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(graphId);
        if (_state.State.GraphIds.Remove(graphId))
        {
            await _state.WriteStateAsync();
        }
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<string>> ListIdsAsync()
    {
        IReadOnlyList<string> snapshot = _state.State.GraphIds.ToArray();
        return new ValueTask<IReadOnlyList<string>>(snapshot);
    }
}
