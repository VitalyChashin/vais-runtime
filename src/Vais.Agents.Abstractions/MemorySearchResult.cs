// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents;

/// <summary>
/// One hit from <see cref="IMemoryStore.SearchAsync"/>. Pairs an item with the key
/// it was stored under and an optional relevance score.
/// </summary>
/// <param name="Key">The key the item was written with. Stable identifier for re-reads.</param>
/// <param name="Item">The matched item.</param>
/// <param name="Score">
/// Optional relevance score in [0, 1]. Higher is better. Vector-backed stores typically
/// set this to cosine similarity; naive substring-matchers may omit it.
/// </param>
public sealed record MemorySearchResult(string Key, MemoryItem Item, float? Score = null);
