// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents;

/// <summary>
/// Caps applied to a single run — the sequence of model invocations, tool calls, and
/// token usage that together produce one final answer. Every cap is optional; unset
/// means unlimited. Carried on <c>StatefulAgentOptions.Budget</c>.
/// </summary>
/// <remarks>
/// <b>Enforcement lands with PR 9</b> of the execution-loop pillar. In v0.4 PR 8 the
/// type ships so consumers can wire it into their options; the outer-loop budget
/// checks arrive in PR 9 when <c>StatefulAiAgent</c> takes over tool-call dispatch
/// and "a run" becomes a well-defined multi-turn concept. Single-turn behaviour in
/// PR 8 is unaffected.
/// </remarks>
/// <param name="MaxTurns">Maximum number of model invocations in a single run.</param>
/// <param name="MaxToolCalls">Maximum number of tool invocations in a single run.</param>
/// <param name="MaxPromptTokens">Total prompt tokens summed across all model invocations in a run.</param>
/// <param name="MaxCompletionTokens">Total completion tokens summed across all model invocations in a run.</param>
/// <param name="MaxDuration">Wall-clock cap on a single run.</param>
public sealed record RunBudget(
    int? MaxTurns = null,
    int? MaxToolCalls = null,
    int? MaxPromptTokens = null,
    int? MaxCompletionTokens = null,
    TimeSpan? MaxDuration = null)
{
    /// <summary>All fields null — no caps applied.</summary>
    public static RunBudget Unlimited { get; } = new();
}
