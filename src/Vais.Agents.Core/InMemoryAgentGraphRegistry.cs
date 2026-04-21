// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace Vais.Agents.Core;

/// <summary>
/// In-memory <see cref="IAgentGraphRegistry"/>. Stores graph manifests keyed by
/// <c>(GraphId, Version)</c>; intended for tests, dev loops, and consumers that
/// don't need cross-process registry durability yet.
/// </summary>
/// <remarks>
/// Thread-safe. Label filtering uses the same prefix-match semantics as
/// <see cref="InMemoryAgentRegistry"/>. Null-version <c>GetAsync</c> returns the
/// lexicographically latest version for the given id.
/// </remarks>
public sealed class InMemoryAgentGraphRegistry : IAgentGraphRegistry
{
    private readonly ConcurrentDictionary<(string Id, string Version), AgentGraphManifest> _manifests = new();

    /// <summary>Register or replace a graph manifest. Returns the manifest for chaining.</summary>
    public AgentGraphManifest Register(AgentGraphManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        _manifests[(manifest.Id, manifest.Version)] = manifest;
        return manifest;
    }

    /// <summary>Remove a graph manifest. Returns <see langword="true"/> if a manifest was present.</summary>
    public bool Remove(string id, string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        return _manifests.TryRemove((id, version), out _);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<AgentGraphManifest> ListAsync(
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
    public ValueTask<AgentGraphManifest?> GetAsync(string id, string? version = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        if (version is not null)
        {
            return ValueTask.FromResult(
                _manifests.TryGetValue((id, version), out var exact) ? exact : null);
        }

        // null version → latest lexicographically-ordered version for this id.
        AgentGraphManifest? latest = null;
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

    private static bool MatchesLabelPrefix(AgentGraphManifest manifest, string prefix)
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
