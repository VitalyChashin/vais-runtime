// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Hosting.Orleans;

/// <summary>
/// Per-extension-id registry grain. Primary key = extension id. Persists container extension
/// manifest YAML payloads keyed by version under the <see cref="AiAgentGrain.StorageName"/> provider.
/// Parallel to <see cref="ContainerPluginRegistryGrain"/> for <c>kind: Extension</c>.
/// </summary>
public sealed class ContainerExtensionRegistryGrain : Grain, IContainerExtensionRegistryGrain
{
    internal const string DirectoryKey = "container-extension-directory";

    private readonly IPersistentState<ContainerExtensionRegistryGrainState> _state;
    private readonly IGrainFactory _grainFactory;

    /// <summary>DI ctor.</summary>
    public ContainerExtensionRegistryGrain(
        [PersistentState("container-extension-registry", AiAgentGrain.StorageName)]
        IPersistentState<ContainerExtensionRegistryGrainState> state,
        IGrainFactory grainFactory)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(grainFactory);
        _state = state;
        _grainFactory = grainFactory;
    }

    /// <inheritdoc />
    public async ValueTask RegisterAsync(string manifestYaml)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestYaml);

        // Parse version from YAML — look for 'version:' line (simple heuristic; full parse not needed here).
        var version = ExtractVersion(manifestYaml) ?? "0.0.0";
        _state.State.ManifestYamlByVersion[version] = manifestYaml;
        _state.State.LatestVersion = version;
        await _state.WriteStateAsync();

        var dir = _grainFactory.GetGrain<IContainerExtensionRegistryDirectoryGrain>(DirectoryKey);
        await dir.TrackAsync(this.GetPrimaryKeyString());
    }

    /// <inheritdoc />
    public ValueTask<string?> GetAsync(string? version)
    {
        if (_state.State.ManifestYamlByVersion.Count == 0)
            return new ValueTask<string?>((string?)null);

        if (version is null)
        {
            var latest = _state.State.LatestVersion;
            if (latest is null || !_state.State.ManifestYamlByVersion.TryGetValue(latest, out var yaml))
                return new ValueTask<string?>((string?)null);
            return new ValueTask<string?>(yaml);
        }

        return new ValueTask<string?>(
            _state.State.ManifestYamlByVersion.TryGetValue(version, out var v) ? v : null);
    }

    /// <inheritdoc />
    public async ValueTask RemoveAsync(string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);

        if (!_state.State.ManifestYamlByVersion.Remove(version))
            return;

        if (string.Equals(_state.State.LatestVersion, version, StringComparison.Ordinal))
            _state.State.LatestVersion = _state.State.ManifestYamlByVersion.Keys.FirstOrDefault();

        await _state.WriteStateAsync();

        if (_state.State.ManifestYamlByVersion.Count == 0)
        {
            var dir = _grainFactory.GetGrain<IContainerExtensionRegistryDirectoryGrain>(DirectoryKey);
            await dir.UntrackAsync(this.GetPrimaryKeyString());
        }
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<string>> ListAsync()
    {
        IReadOnlyList<string> snapshot = _state.State.ManifestYamlByVersion.Values.ToArray();
        return new ValueTask<IReadOnlyList<string>>(snapshot);
    }

    private static string? ExtractVersion(string yaml)
    {
        foreach (var line in yaml.AsSpan().EnumerateLines())
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("version:", StringComparison.Ordinal))
            {
                var val = trimmed[8..].Trim();
                return val.Trim('"').ToString();
            }
        }
        return null;
    }
}

/// <summary>
/// Singleton directory grain tracking all registered container extension ids.
/// </summary>
public sealed class ContainerExtensionRegistryDirectoryGrain : Grain, IContainerExtensionRegistryDirectoryGrain
{
    private readonly IPersistentState<ContainerExtensionRegistryDirectoryGrainState> _state;

    /// <summary>DI ctor.</summary>
    public ContainerExtensionRegistryDirectoryGrain(
        [PersistentState("container-extension-directory", AiAgentGrain.StorageName)]
        IPersistentState<ContainerExtensionRegistryDirectoryGrainState> state)
    {
        ArgumentNullException.ThrowIfNull(state);
        _state = state;
    }

    /// <inheritdoc />
    public async ValueTask TrackAsync(string extensionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extensionId);
        if (_state.State.ExtensionIds.Add(extensionId))
            await _state.WriteStateAsync();
    }

    /// <inheritdoc />
    public async ValueTask UntrackAsync(string extensionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extensionId);
        if (_state.State.ExtensionIds.Remove(extensionId))
            await _state.WriteStateAsync();
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<string>> ListIdsAsync()
    {
        IReadOnlyList<string> snapshot = _state.State.ExtensionIds.ToArray();
        return new ValueTask<IReadOnlyList<string>>(snapshot);
    }
}
