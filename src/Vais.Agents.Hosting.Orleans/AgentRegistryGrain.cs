// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Hosting.Orleans;

/// <summary>
/// Per-agent-id registry grain. Primary key = agent id. Persists manifest
/// JSON payloads keyed by version under the <see cref="AiAgentGrain.StorageName"/>
/// provider so v0.17 shares the same Redis / Postgres / memory grain-storage
/// wiring as the agent grains themselves.
/// </summary>
/// <remarks>
/// Grain-interface payloads are JSON strings rather than <c>AgentManifest</c>
/// records because <c>Vais.Agents.Abstractions</c> stays Orleans-free (no
/// <c>[GenerateSerializer]</c> attributes). The grain converts between string
/// and <see cref="AgentManifest"/> internally; <see cref="OrleansAgentRegistry"/>
/// is the round-trip helper at the service boundary.
/// </remarks>
public sealed class AgentRegistryGrain : Grain, IAgentRegistryGrain
{
    private readonly IPersistentState<AgentRegistryGrainState> _state;
    private readonly IGrainFactory _grainFactory;

    /// <summary>DI ctor — grain-storage injection + cross-grain calls.</summary>
    public AgentRegistryGrain(
        [PersistentState("registry", AiAgentGrain.StorageName)] IPersistentState<AgentRegistryGrainState> state,
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

        var manifest = OrleansAgentRegistry.DeserializeManifest(manifestJson);

        var id = this.GetPrimaryKeyString();
        if (!string.Equals(id, manifest.Id, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Manifest id mismatch: grain key is '{id}' but manifest.Id is '{manifest.Id}'.");
        }

        _state.State.ManifestJsonByVersion[manifest.Version] = manifestJson;
        _state.State.LatestVersion = manifest.Version;
        await _state.WriteStateAsync();

        var directory = _grainFactory.GetGrain<IAgentRegistryDirectoryGrain>(OrleansAgentRegistry.DirectoryKey);
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
            var directory = _grainFactory.GetGrain<IAgentRegistryDirectoryGrain>(OrleansAgentRegistry.DirectoryKey);
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
/// <see cref="OrleansAgentRegistry.DirectoryKey"/>. Maintains the set of
/// agent ids that have at least one registered manifest.
/// </summary>
public sealed class AgentRegistryDirectoryGrain : Grain, IAgentRegistryDirectoryGrain
{
    private readonly IPersistentState<AgentRegistryDirectoryGrainState> _state;

    /// <summary>DI ctor — grain-storage injection.</summary>
    public AgentRegistryDirectoryGrain(
        [PersistentState("directory", AiAgentGrain.StorageName)] IPersistentState<AgentRegistryDirectoryGrainState> state)
    {
        ArgumentNullException.ThrowIfNull(state);
        _state = state;
    }

    /// <inheritdoc />
    public async ValueTask TrackAsync(string agentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        if (_state.State.AgentIds.Add(agentId))
        {
            await _state.WriteStateAsync();
        }
    }

    /// <inheritdoc />
    public async ValueTask UntrackAsync(string agentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        if (_state.State.AgentIds.Remove(agentId))
        {
            await _state.WriteStateAsync();
        }
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<string>> ListIdsAsync()
    {
        IReadOnlyList<string> snapshot = _state.State.AgentIds.ToArray();
        return new ValueTask<IReadOnlyList<string>>(snapshot);
    }
}
