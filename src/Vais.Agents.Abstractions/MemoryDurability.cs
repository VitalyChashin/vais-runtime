// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents;

/// <summary>
/// Durability classes for <see cref="IMemoryStore"/> items. Implementations may apply
/// different retention, eviction, or backing-store strategies per class; the enum
/// itself only carries the intent.
/// </summary>
public enum MemoryDurability
{
    /// <summary>
    /// In-process-lifetime state — not persisted across restarts. Equivalent to a
    /// per-turn scratchpad.
    /// </summary>
    ShortTerm = 0,

    /// <summary>
    /// Durable beyond the current session. Equivalent to CrewAI's LongTermMemory or
    /// Mastra's resource-scoped memory.
    /// </summary>
    LongTerm = 1,

    /// <summary>
    /// Session-scoped but durably persisted — e.g. Mastra's "working memory" concept:
    /// state the agent maintains across turns within a session but that outlives any
    /// single in-process activation.
    /// </summary>
    Working = 2,
}
