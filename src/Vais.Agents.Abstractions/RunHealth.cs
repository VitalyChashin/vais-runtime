// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents;

/// <summary>
/// Run-level rollup of mechanical-failure health. Answers the question a green
/// <c>graph.completed</c> cannot: <em>"this run finished — but was it healthy?"</em>
/// A run is the worst level among all of its descendants (across the whole nesting tree:
/// router → domain → sub-domain, plus background sub-runs), so a single recovered
/// <see cref="FailureLevel.Warning"/> leaf marks an otherwise-green run as
/// <see cref="RunHealthLevel.Degraded"/> rather than letting the recovery hide it.
/// </summary>
/// <remarks>
/// This is the machine-readable form of "verify node outputs, not <c>graph.completed</c>":
/// the leaf-failure list attributes each mechanical failure to the sub-agent / node / tool
/// that produced it, so an operator can tell a quality regression apart from a mechanical one.
/// </remarks>
public enum RunHealthLevel
{
    /// <summary>No mechanical failures detected anywhere in the run tree.</summary>
    Healthy = 0,

    /// <summary>At least one recovered mechanical failure (retry, fallback, tool error fed back, partial). Completed, but degraded.</summary>
    Degraded = 1,

    /// <summary>At least one unrecovered failure that aborted a turn, node, or sub-run.</summary>
    Failed = 2,
}

/// <summary>
/// The kind of mechanical-failure signal a <see cref="RunHealthSignal"/> represents.
/// Distinct from quality: every kind here is a <em>mechanical</em> reason a run degraded,
/// not a low eval score.
/// </summary>
public enum RunHealthSignalKind
{
    /// <summary>A tool invocation failed (typically fed back to the model — recovered).</summary>
    ToolError = 0,

    /// <summary>An MCP gateway tool call failed.</summary>
    McpError = 1,

    /// <summary>An outgoing LLM call was retried after a transient failure.</summary>
    LlmRetry = 2,

    /// <summary>An LLM call fell back from one provider to another after the primary failed.</summary>
    LlmFallback = 3,

    /// <summary>An agent turn failed with an exception before completion.</summary>
    TurnFailed = 4,

    /// <summary>A turn completed but delivered a degraded/partial result (e.g. a plugin <c>is_partial</c>).</summary>
    TurnPartial = 5,

    /// <summary>A guardrail (input, tool, or output) blocked or modified an operation.</summary>
    Guardrail = 6,

    /// <summary>A graph node failed.</summary>
    NodeFailed = 7,

    /// <summary>An LLM call failed at the gateway (sourced from the gateway event store, not the bus).</summary>
    LlmError = 8,
}

/// <summary>
/// A single attributed mechanical-failure signal within a run tree — one needle in the
/// "all sub-agents scored well but the chain degraded" haystack.
/// </summary>
/// <param name="Source">Attribution: the sub-agent name, graph node id, or tool name that produced the signal.</param>
/// <param name="Kind">What kind of mechanical failure this is.</param>
/// <param name="Level">Severity: <see cref="FailureLevel.Warning"/> for recovered, <see cref="FailureLevel.Error"/> for fatal.</param>
/// <param name="ErrorType">CLR/error type name when known; otherwise <see langword="null"/>.</param>
/// <param name="IsTransient">Whether the underlying failure was classified transient (retryable).</param>
/// <param name="At">UTC timestamp of the signal.</param>
public sealed record RunHealthSignal(
    string Source,
    RunHealthSignalKind Kind,
    FailureLevel Level,
    string? ErrorType,
    bool IsTransient,
    DateTimeOffset At);

/// <summary>
/// The per-run health rollup: the worst level across the run tree, the attributed leaf-failure
/// list, and any failures rolled up from background sub-runs.
/// </summary>
/// <param name="RunId">The root run identifier this health rolls up to.</param>
/// <param name="Level">Worst level across all in-run and background signals.</param>
/// <param name="Signals">Attributed mechanical-failure signals within the synchronous run tree.</param>
/// <param name="BackgroundFailures">Failures rolled up from background (fire-and-forget) sub-runs launched by this run.</param>
public sealed record RunHealth(
    string RunId,
    RunHealthLevel Level,
    IReadOnlyList<RunHealthSignal> Signals,
    IReadOnlyList<RunHealthSignal> BackgroundFailures)
{
    /// <summary>A healthy run with no signals.</summary>
    public static RunHealth Healthy(string runId) =>
        new(runId, RunHealthLevel.Healthy, [], []);

    /// <summary>
    /// Maps a per-signal <see cref="FailureLevel"/> to the run-level <see cref="RunHealthLevel"/>:
    /// <see cref="FailureLevel.Default"/>→<see cref="RunHealthLevel.Healthy"/>,
    /// <see cref="FailureLevel.Warning"/>→<see cref="RunHealthLevel.Degraded"/>,
    /// <see cref="FailureLevel.Error"/>→<see cref="RunHealthLevel.Failed"/>.
    /// </summary>
    public static RunHealthLevel ToRunHealthLevel(FailureLevel level) => level switch
    {
        FailureLevel.Error => RunHealthLevel.Failed,
        FailureLevel.Warning => RunHealthLevel.Degraded,
        _ => RunHealthLevel.Healthy,
    };
}
