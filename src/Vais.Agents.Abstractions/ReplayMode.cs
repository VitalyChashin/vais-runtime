// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents;

/// <summary>
/// Controls the granularity of replay when a streaming run is resumed via
/// <see cref="IAgentJournal"/>.
/// </summary>
public enum ReplayMode
{
    /// <summary>
    /// Replay only tool-call outcomes from the journal. The provider is re-invoked
    /// for fresh LLM completion on resume. This is the default behavior in v0.5
    /// through v0.20 and provides tool-call cache-replay without delta fidelity.
    /// </summary>
    ToolOnly = 0,

    /// <summary>
    /// Replay both tool-call outcomes and completion deltas from the journal.
    /// The provider is bypassed entirely on resume; the exact delta sequence
    /// from the original run is re-yielded verbatim. Enables deterministic
    /// delta-by-delta reproduction of streaming runs at the cost of additional
    /// journal storage.
    /// </summary>
    Full = 1
}
