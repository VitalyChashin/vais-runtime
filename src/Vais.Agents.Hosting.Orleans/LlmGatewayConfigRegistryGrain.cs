// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Hosting.Orleans;

/// <summary>
/// Per-config-id registry grain. Primary key = config id. Persists manifest JSON
/// payloads keyed by version under the <see cref="AiAgentGrain.StorageName"/>
/// provider, sharing grain-storage wiring with agent and graph grains.
/// </summary>
public sealed class LlmGatewayConfigRegistryGrain : Grain, ILlmGatewayConfigRegistryGrain
{
    private readonly IPersistentState<LlmGatewayConfigRegistryGrainState> _state;
    private readonly IGrainFactory _grainFactory;

    /// <summary>DI ctor — grain-storage injection + cross-grain calls.</summary>
    public LlmGatewayConfigRegistryGrain(
        [PersistentState("llm-gateway-config-registry", AiAgentGrain.StorageName)] IPersistentState<LlmGatewayConfigRegistryGrainState> state,
        IGrainFactory grainFactory)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(grainFactory);
        _state = state;
        _grainFactory = grainFactory;
    }

    /// <inheritdoc />
    public async ValueTask RegisterAsync(string manifestJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestJson);

        var manifest = OrleansLlmGatewayConfigRegistry.DeserializeManifest(manifestJson);
        _state.State.ManifestJsonByVersion[manifest.Version] = manifestJson;
        _state.State.LatestVersion = manifest.Version;
        await _state.WriteStateAsync();

        var directory = _grainFactory.GetGrain<ILlmGatewayConfigRegistryDirectoryGrain>(OrleansLlmGatewayConfigRegistry.DirectoryKey);
        await directory.TrackAsync(this.GetPrimaryKeyString());
    }

    /// <inheritdoc />
    public ValueTask<string?> GetAsync(string? version)
    {
        if (_state.State.ManifestJsonByVersion.Count == 0)
            return new ValueTask<string?>((string?)null);

        if (version is null)
        {
            var latest = _state.State.LatestVersion;
            if (latest is null || !_state.State.ManifestJsonByVersion.TryGetValue(latest, out var json))
                return new ValueTask<string?>((string?)null);
            return new ValueTask<string?>(json);
        }

        return new ValueTask<string?>(
            _state.State.ManifestJsonByVersion.TryGetValue(version, out var versioned) ? versioned : null);
    }

    /// <inheritdoc />
    public async ValueTask RemoveAsync(string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);

        if (!_state.State.ManifestJsonByVersion.Remove(version))
            return;

        if (string.Equals(_state.State.LatestVersion, version, StringComparison.Ordinal))
            _state.State.LatestVersion = _state.State.ManifestJsonByVersion.Keys.FirstOrDefault();

        await _state.WriteStateAsync();

        if (_state.State.ManifestJsonByVersion.Count == 0)
        {
            var directory = _grainFactory.GetGrain<ILlmGatewayConfigRegistryDirectoryGrain>(OrleansLlmGatewayConfigRegistry.DirectoryKey);
            await directory.UntrackAsync(this.GetPrimaryKeyString());
        }
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<string>> ListAsync()
    {
        IReadOnlyList<string> snapshot = _state.State.ManifestJsonByVersion.Values.ToArray();
        return new ValueTask<IReadOnlyList<string>>(snapshot);
    }
}

/// <summary>
/// Singleton directory grain tracking the set of LLM gateway config ids registered in the cluster.
/// </summary>
public sealed class LlmGatewayConfigRegistryDirectoryGrain : Grain, ILlmGatewayConfigRegistryDirectoryGrain
{
    private readonly IPersistentState<LlmGatewayConfigRegistryDirectoryGrainState> _state;

    /// <summary>DI ctor — grain-storage injection.</summary>
    public LlmGatewayConfigRegistryDirectoryGrain(
        [PersistentState("llm-gateway-config-directory", AiAgentGrain.StorageName)] IPersistentState<LlmGatewayConfigRegistryDirectoryGrainState> state)
    {
        ArgumentNullException.ThrowIfNull(state);
        _state = state;
    }

    /// <inheritdoc />
    public async ValueTask TrackAsync(string configId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configId);
        if (_state.State.ConfigIds.Add(configId))
            await _state.WriteStateAsync();
    }

    /// <inheritdoc />
    public async ValueTask UntrackAsync(string configId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configId);
        if (_state.State.ConfigIds.Remove(configId))
            await _state.WriteStateAsync();
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<string>> ListIdsAsync()
    {
        IReadOnlyList<string> snapshot = _state.State.ConfigIds.ToArray();
        return new ValueTask<IReadOnlyList<string>>(snapshot);
    }
}
