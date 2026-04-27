// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace Vais.Agents.Core;

/// <summary>
/// In-memory <see cref="IMcpServerRegistry"/>. Stores manifests keyed by
/// <c>(Id, Version)</c>; intended for tests, dev loops, and consumers that
/// don't need cross-process registry durability.
/// </summary>
/// <remarks>
/// Thread-safe. Label filtering uses the same prefix-match semantics as
/// <see cref="InMemoryAgentGraphRegistry"/>. Null-version <c>GetAsync</c> returns the
/// lexicographically latest version for the given id.
/// </remarks>
public sealed class InMemoryMcpServerRegistry : IMcpServerRegistry
{
    private readonly ConcurrentDictionary<(string Id, string Version), McpServerManifest> _manifests = new();

    /// <inheritdoc />
    public async IAsyncEnumerable<McpServerManifest> ListAsync(
        string? labelPrefix = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask.ConfigureAwait(false);

        foreach (var manifest in _manifests.Values)
        {
            ct.ThrowIfCancellationRequested();
            if (labelPrefix is null || MatchesLabelPrefix(manifest, labelPrefix))
            {
                yield return manifest;
            }
        }
    }

    /// <inheritdoc />
    public ValueTask<McpServerManifest?> GetAsync(string id, string? version = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        if (version is not null)
        {
            return ValueTask.FromResult(
                _manifests.TryGetValue((id, version), out var exact) ? exact : null);
        }

        McpServerManifest? latest = null;
        foreach (var entry in _manifests)
        {
            if (!string.Equals(entry.Key.Id, id, StringComparison.Ordinal)) continue;
            if (latest is null || string.CompareOrdinal(entry.Value.Version, latest.Version) > 0)
            {
                latest = entry.Value;
            }
        }
        return ValueTask.FromResult(latest);
    }

    /// <inheritdoc />
    public ValueTask RegisterAsync(McpServerManifest manifest, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        _manifests[(manifest.Id, manifest.Version)] = manifest;
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask RemoveAsync(string id, string version, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        _manifests.TryRemove((id, version), out _);
        return ValueTask.CompletedTask;
    }

    private static bool MatchesLabelPrefix(McpServerManifest manifest, string prefix)
    {
        if (manifest.Labels is null || manifest.Labels.Count == 0) return false;
        foreach (var (key, value) in manifest.Labels)
        {
            if (key.StartsWith(prefix, StringComparison.Ordinal)) return true;
            if ($"{key}:{value}".StartsWith(prefix, StringComparison.Ordinal)) return true;
        }
        return false;
    }
}
