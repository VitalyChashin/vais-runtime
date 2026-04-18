// Copyright (c) 2026 VAIS2 Platform contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais2.Agents;

/// <summary>
/// A single item written to an <see cref="IMemoryStore"/>. Items are addressed by
/// a <see cref="MemoryScope"/> and a key (supplied to <see cref="IMemoryStore.WriteAsync"/>);
/// the item itself carries its content and optional metadata.
/// </summary>
/// <param name="Content">The stored text. Implementations may index this for <see cref="IMemoryStore.SearchAsync"/>.</param>
/// <param name="Metadata">Optional flat key/value metadata. Kept as strings to be durable across serialisers.</param>
/// <param name="CreatedAt">Optional wall-clock timestamp of when the item was produced by the caller.</param>
public sealed record MemoryItem(
    string Content,
    IReadOnlyDictionary<string, string>? Metadata = null,
    DateTimeOffset? CreatedAt = null);
