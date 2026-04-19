// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace Vais.Agents.Core;

/// <summary>
/// In-memory <see cref="IAgentRegistry"/>. Stores manifests keyed by
/// <c>(AgentId, Version)</c>; intended for tests, dev loops, and consumers who
/// don't need cross-process registry durability yet.
/// </summary>
/// <remarks>
/// Thread-safe. Label filtering is simple prefix matching — a label entry is a
/// match when any key (or the <c>"key:value"</c> concatenation) starts with the
/// supplied prefix. Production registries typically want structured label
/// selectors; the shape here keeps the v0.4 surface simple.
/// </remarks>
public sealed class InMemoryAgentRegistry : IAgentRegistry
{
    private readonly ConcurrentDictionary<(string Id, string Version), AgentManifest> _manifests = new();

    /// <summary>Register or replace a manifest. Returns the added manifest for chaining.</summary>
    public AgentManifest Register(AgentManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        _manifests[(manifest.Id, manifest.Version)] = manifest;
        return manifest;
    }

    /// <summary>Remove a manifest. Returns true if a manifest was present.</summary>
    public bool Remove(string id, string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        return _manifests.TryRemove((id, version), out _);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<AgentManifest> ListAsync(
        string? labelPrefix = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask.ConfigureAwait(false);

        foreach (var manifest in _manifests.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (labelPrefix is null || MatchesLabelPrefix(manifest, labelPrefix))
            {
                yield return manifest;
            }
        }
    }

    /// <inheritdoc />
    public ValueTask<AgentManifest?> GetAsync(string id, string? version = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        if (version is not null)
        {
            return ValueTask.FromResult(
                _manifests.TryGetValue((id, version), out var exact) ? exact : null);
        }

        // null version → latest lexicographically ordered version for this id.
        AgentManifest? latest = null;
        foreach (var entry in _manifests)
        {
            if (!string.Equals(entry.Key.Id, id, StringComparison.Ordinal))
            {
                continue;
            }
            if (latest is null || string.CompareOrdinal(entry.Value.Version, latest.Version) > 0)
            {
                latest = entry.Value;
            }
        }
        return ValueTask.FromResult(latest);
    }

    private static bool MatchesLabelPrefix(AgentManifest manifest, string prefix)
    {
        if (manifest.Labels is null || manifest.Labels.Count == 0)
        {
            return false;
        }
        foreach (var (key, value) in manifest.Labels)
        {
            if (key.StartsWith(prefix, StringComparison.Ordinal))
            {
                return true;
            }
            if ($"{key}:{value}".StartsWith(prefix, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }
}
