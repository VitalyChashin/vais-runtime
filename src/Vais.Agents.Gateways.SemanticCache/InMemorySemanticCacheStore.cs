// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Collections.Concurrent;

namespace Vais.Agents.Gateways.SemanticCache;

/// <summary>
/// In-process, exact-match implementation of <see cref="ISemanticCacheStore"/>.
/// Thread-safe via <see cref="ConcurrentDictionary{TKey,TValue}"/>.
/// </summary>
public sealed class InMemorySemanticCacheStore : ISemanticCacheStore
{
    private readonly ConcurrentDictionary<string, CompletionResponse> _cache = new();

    /// <inheritdoc/>
    public ValueTask<CompletionResponse?> GetAsync(string key, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(_cache.TryGetValue(key, out var response) ? response : null);

    /// <inheritdoc/>
    public ValueTask SetAsync(string key, CompletionResponse response, CancellationToken cancellationToken = default)
    {
        _cache[key] = response;
        return ValueTask.CompletedTask;
    }
}
