// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Hosting.Orleans;

/// <summary>
/// Persisted state for <see cref="IdempotencyKeyGrain"/>. Hosts a single
/// <see cref="IdempotencyKeySurrogate"/> plus a <see cref="HasEntry"/> flag
/// so the grain can distinguish "never reserved" from "reserved + released",
/// matching the v0.8/v0.9 convention.
/// </summary>
[GenerateSerializer]
public sealed class IdempotencyKeyGrainState
{
    /// <summary>Whether an entry has been saved under this grain id.</summary>
    [Id(0)]
    public bool HasEntry { get; set; }

    /// <summary>The persisted entry. Meaningful only when <see cref="HasEntry"/> is true.</summary>
    [Id(1)]
    public IdempotencyKeySurrogate Entry { get; set; }
}
