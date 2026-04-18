// Copyright (c) 2026 VAIS2 Platform contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais2.Agents.Core;

/// <summary>
/// Called after each <see cref="OrchestrationStep"/> by a round-based orchestrator
/// to decide whether the conversation should stop early. Return <c>true</c> to
/// stop before the next participant runs.
/// </summary>
/// <param name="steps">All steps emitted so far, in order. The most recent is the last element.</param>
/// <returns><c>true</c> to terminate the orchestration; <c>false</c> to continue.</returns>
/// <remarks>
/// Keep the predicate pure and fast — it runs synchronously between participant
/// turns. Consumers that want LLM-driven termination should wrap the orchestrator
/// rather than doing the LLM call inside the predicate, which would block the
/// orchestration loop on every turn.
/// </remarks>
public delegate bool TerminationPredicate(IReadOnlyList<OrchestrationStep> steps);
