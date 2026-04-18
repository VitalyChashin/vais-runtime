// Copyright (c) 2026 VAIS2 Platform contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Runtime.CompilerServices;

namespace Vais2.Agents.Core;

/// <summary>
/// No-op <see cref="IMemoryStore"/>. Writes are silently dropped; reads always return null;
/// search yields nothing. Used when consumers haven't wired a store.
/// </summary>
public sealed class NullMemoryStore : IMemoryStore
{
    /// <summary>Shared singleton instance. Stateless.</summary>
    public static readonly NullMemoryStore Instance = new();

    private NullMemoryStore() { }

    /// <inheritdoc />
    public ValueTask WriteAsync(MemoryScope scope, string key, MemoryItem item, CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;

    /// <inheritdoc />
    public ValueTask<MemoryItem?> ReadAsync(MemoryScope scope, string key, CancellationToken cancellationToken = default)
        => ValueTask.FromResult<MemoryItem?>(null);

    /// <inheritdoc />
    public async IAsyncEnumerable<MemorySearchResult> SearchAsync(
        MemoryScope scope,
        string query,
        int topK = 5,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        yield break;
    }

    /// <inheritdoc />
    public ValueTask<bool> DeleteAsync(MemoryScope scope, string key, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(false);
}
