// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Gateways.SemanticCache;

/// <summary>
/// Cache store used by <see cref="LlmSemanticCacheMiddleware"/>. Implementations
/// determine the matching strategy (exact string, vector similarity, etc.).
/// </summary>
public interface ISemanticCacheStore
{
    /// <summary>
    /// Returns a cached response for the given key, or <see langword="null"/> if no entry exists.
    /// </summary>
    ValueTask<CompletionResponse?> GetAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores a response under the given key.
    /// </summary>
    ValueTask SetAsync(string key, CompletionResponse response, CancellationToken cancellationToken = default);
}
