// Copyright (c) 2026 VAIS2 Platform contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais2.Agents;

/// <summary>
/// Pluggable long-term / working memory store. Scopes partition the key space;
/// items in different scopes never collide on a key.
/// </summary>
/// <remarks>
/// <para>
/// The contract covers the minimum viable shape: <see cref="WriteAsync"/>,
/// <see cref="ReadAsync"/>, <see cref="SearchAsync"/>, <see cref="DeleteAsync"/>.
/// List-all and scoped-enumeration operations are deliberately omitted — callers who
/// need them should build on <see cref="SearchAsync"/> with an empty query or wait
/// for a richer extension surface in a later release.
/// </para>
/// <para>
/// Search semantics are implementation-defined. The in-process default does naive
/// substring matching; vector-store-backed implementations perform semantic search.
/// The <see cref="MemorySearchResult.Score"/> field is optional precisely because the
/// contract does not require a particular scoring model.
/// </para>
/// </remarks>
public interface IMemoryStore
{
    /// <summary>
    /// Write (or overwrite) an item under <paramref name="scope"/> at <paramref name="key"/>.
    /// </summary>
    ValueTask WriteAsync(MemoryScope scope, string key, MemoryItem item, CancellationToken cancellationToken = default);

    /// <summary>
    /// Read the item at (<paramref name="scope"/>, <paramref name="key"/>) or null if absent.
    /// </summary>
    ValueTask<MemoryItem?> ReadAsync(MemoryScope scope, string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Return up to <paramref name="topK"/> items in <paramref name="scope"/> matching
    /// <paramref name="query"/>, ranked best-first.
    /// </summary>
    IAsyncEnumerable<MemorySearchResult> SearchAsync(MemoryScope scope, string query, int topK = 5, CancellationToken cancellationToken = default);

    /// <summary>
    /// Remove the item at (<paramref name="scope"/>, <paramref name="key"/>). Returns true
    /// if an item was removed, false if the key was absent.
    /// </summary>
    ValueTask<bool> DeleteAsync(MemoryScope scope, string key, CancellationToken cancellationToken = default);
}
