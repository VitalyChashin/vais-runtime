// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents;

/// <summary>
/// Registry of <see cref="LlmGatewayConfigManifest"/> records.
/// Separate from the lifecycle manager so consumers can wire a read-only registry
/// without a full lifecycle implementation.
/// </summary>
/// <remarks>
/// <see cref="RegisterAsync"/> and <see cref="RemoveAsync"/> are write-side; called
/// only from the lifecycle manager. <see cref="GetAsync"/> returns null on miss.
/// </remarks>
public interface ILlmGatewayConfigRegistry
{
    /// <summary>Enumerate manifests, optionally filtered by a label prefix (e.g., "team:").</summary>
    IAsyncEnumerable<LlmGatewayConfigManifest> ListAsync(
        string? labelPrefix = null, CancellationToken ct = default);

    /// <summary>Fetch a specific manifest. Null <paramref name="version"/> returns the latest-lexicographically version. Returns null on miss.</summary>
    ValueTask<LlmGatewayConfigManifest?> GetAsync(
        string id, string? version = null, CancellationToken ct = default);

    /// <summary>Register (upsert) a manifest. Called by the lifecycle manager.</summary>
    ValueTask RegisterAsync(LlmGatewayConfigManifest manifest, CancellationToken ct = default);

    /// <summary>Remove a manifest by id+version. No-op when not found (idempotent).</summary>
    ValueTask RemoveAsync(string id, string version, CancellationToken ct = default);
}
