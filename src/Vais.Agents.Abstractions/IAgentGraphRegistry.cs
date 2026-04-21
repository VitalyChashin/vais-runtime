// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents;

/// <summary>
/// Read-only catalog of deployed <see cref="AgentGraphManifest"/> objects. Mirrors
/// <see cref="IAgentRegistry"/> for graph manifests; implementations range from in-memory
/// (tests / dev) to Orleans-backed (production) to remote HTTP.
/// </summary>
public interface IAgentGraphRegistry
{
    /// <summary>
    /// Enumerate all graph manifests, optionally filtered to those whose labels
    /// contain a key (or <c>key:value</c> string) that starts with
    /// <paramref name="labelPrefix"/>. Pass <see langword="null"/> to enumerate all.
    /// </summary>
    IAsyncEnumerable<AgentGraphManifest> ListAsync(
        string? labelPrefix = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieve a single manifest by <paramref name="id"/> and optional
    /// <paramref name="version"/>. When <paramref name="version"/> is
    /// <see langword="null"/> the registry returns the latest version (lexicographic
    /// ordering). Returns <see langword="null"/> when no matching manifest exists.
    /// </summary>
    ValueTask<AgentGraphManifest?> GetAsync(
        string id,
        string? version = null,
        CancellationToken cancellationToken = default);
}
