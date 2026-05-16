// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Hosting.Orleans;

/// <summary>
/// Per-suite-id registry grain. Primary key = suite id. Persists eval suite manifest JSON
/// payloads keyed by version under the <see cref="AiAgentGrain.StorageName"/> provider.
/// </summary>
public sealed class EvalSuiteRegistryGrain : Grain, IEvalSuiteRegistryGrain
{
    private readonly IPersistentState<EvalSuiteRegistryGrainState> _state;
    private readonly IGrainFactory _grainFactory;

    /// <summary>DI ctor — grain-storage injection + cross-grain calls.</summary>
    public EvalSuiteRegistryGrain(
        [PersistentState("eval-suite-registry", AiAgentGrain.StorageName)] IPersistentState<EvalSuiteRegistryGrainState> state,
        IGrainFactory grainFactory)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(grainFactory);
        _state = state;
        _grainFactory = grainFactory;
    }

    /// <inheritdoc />
    public async ValueTask UpsertAsync(string manifestJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestJson);

        var manifest = OrleansEvalSuiteRegistry.DeserializeManifest(manifestJson);
        _state.State.ManifestJsonByVersion[manifest.Version] = manifestJson;
        _state.State.LatestVersion = manifest.Version;
        await _state.WriteStateAsync();

        var directory = _grainFactory.GetGrain<IEvalSuiteRegistryDirectoryGrain>(OrleansEvalSuiteRegistry.DirectoryKey);
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
            var directory = _grainFactory.GetGrain<IEvalSuiteRegistryDirectoryGrain>(OrleansEvalSuiteRegistry.DirectoryKey);
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
/// Singleton directory grain tracking the set of eval suite ids registered in the cluster.
/// </summary>
public sealed class EvalSuiteRegistryDirectoryGrain : Grain, IEvalSuiteRegistryDirectoryGrain
{
    private readonly IPersistentState<EvalSuiteRegistryDirectoryGrainState> _state;

    /// <summary>DI ctor — grain-storage injection.</summary>
    public EvalSuiteRegistryDirectoryGrain(
        [PersistentState("eval-suite-directory", AiAgentGrain.StorageName)] IPersistentState<EvalSuiteRegistryDirectoryGrainState> state)
    {
        ArgumentNullException.ThrowIfNull(state);
        _state = state;
    }

    /// <inheritdoc />
    public async ValueTask TrackAsync(string suiteId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(suiteId);
        if (_state.State.SuiteIds.Add(suiteId))
            await _state.WriteStateAsync();
    }

    /// <inheritdoc />
    public async ValueTask UntrackAsync(string suiteId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(suiteId);
        if (_state.State.SuiteIds.Remove(suiteId))
            await _state.WriteStateAsync();
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<string>> ListIdsAsync()
    {
        IReadOnlyList<string> snapshot = _state.State.SuiteIds.ToArray();
        return new ValueTask<IReadOnlyList<string>>(snapshot);
    }
}
