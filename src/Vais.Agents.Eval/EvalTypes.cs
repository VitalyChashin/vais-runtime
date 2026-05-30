// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace Vais.Agents.Eval;

/// <summary>A single eval assertion bound to a concrete <see cref="EvalCase"/>.</summary>
public interface IEvalAssertion
{
    /// <summary>Discriminator string that matches the factory kind key.</summary>
    string Kind { get; }

    /// <summary>Evaluate the assertion against the completed agent run.</summary>
    ValueTask<EvalAssertionResult> EvaluateAsync(EvalCaseContext ctx, EvalRunRecord run, CancellationToken ct);
}

/// <summary>Outcome of a single assertion evaluation.</summary>
public sealed record EvalAssertionResult(
    EvalAssertionStatus Status,
    double? Score,
    string? Reason,
    IReadOnlyDictionary<string, JsonElement>? Diagnostics = null);

/// <summary>Evaluation outcome for a single assertion.</summary>
public enum EvalAssertionStatus
{
    /// <summary>Assertion passed.</summary>
    Pass = 0,
    /// <summary>Assertion failed (score below threshold or regex did not match).</summary>
    Fail = 1,
    /// <summary>Assertion was skipped (e.g. required data was absent).</summary>
    Skipped = 2,
    /// <summary>Assertion threw an exception during evaluation.</summary>
    Error = 3,
}

/// <summary>Ambient context passed to each assertion during evaluation.</summary>
public sealed record EvalCaseContext(EvalCase Case, EvalSuiteSpec Suite, AgentContext AgentContext);

/// <summary>Captured result of a single agent/graph invocation for evaluation.</summary>
public sealed record EvalRunRecord(
    string AgentRunId,
    string ResponseText,
    JsonElement? ResponseJson,
    IReadOnlyList<JournalEntry> JournalEntries,
    IReadOnlyList<AgentEvent> Events,
    IReadOnlyDictionary<string, JsonElement>? FinalState,
    TimeSpan Duration,
    int? PromptTokens,
    int? CompletionTokens);

/// <summary>Creates an <see cref="IEvalAssertion"/> from a JSON params blob.</summary>
public interface IEvalAssertionFactory
{
    /// <summary>Assertion kind string (e.g. <c>"judge-score"</c>). Case-insensitive match against manifest.</summary>
    string Kind { get; }
    /// <summary>Create an assertion instance from the raw JSON <paramref name="args"/> element.</summary>
    IEvalAssertion Create(JsonElement args, IServiceProvider services);
}

/// <summary>Registry of known <see cref="IEvalAssertionFactory"/> instances keyed by kind.</summary>
public interface IEvalAssertionFactoryRegistry
{
    /// <summary>Try to locate a factory by its kind string. Returns <see langword="false"/> if not registered.</summary>
    bool TryGet(string kind, [NotNullWhen(true)] out IEvalAssertionFactory? factory);
    /// <summary>All registered assertion kind strings.</summary>
    IReadOnlyList<string> RegisteredKinds { get; }
}

// ── Result store DTOs ─────────────────────────────────────────────────────────

/// <summary>Lifecycle status of an eval run.</summary>
public enum EvalRunStatus
{
    /// <summary>Run created but not yet started.</summary>
    Pending = 0,
    /// <summary>Run is in progress.</summary>
    Running = 1,
    /// <summary>All cases processed normally.</summary>
    Completed = 2,
    /// <summary>Run aborted due to an unhandled error.</summary>
    Failed = 3,
    /// <summary>Run was cancelled by the user.</summary>
    Cancelled = 4,
}

/// <summary>Pass/fail/error status for a single eval case.</summary>
public enum EvalCaseStatus
{
    /// <summary>All assertions passed.</summary>
    Pass = 0,
    /// <summary>One or more assertions failed.</summary>
    Fail = 1,
    /// <summary>The agent invocation or assertion evaluation threw.</summary>
    Error = 2,
}

/// <summary>Header-level summary of a completed or in-progress eval run.</summary>
public sealed record EvalRunSummary(
    string EvalRunId,
    string SuiteName,
    string SuiteVersion,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    EvalRunStatus Status,
    int TotalCases,
    int PassedCases,
    int FailedCases,
    string Source = "batch",
    DateTimeOffset? WindowStart = null,
    DateTimeOffset? WindowEnd = null);

/// <summary>Full detail of an eval run including per-case and per-assertion results.</summary>
public sealed record EvalRunDetail(EvalRunSummary Summary, IReadOnlyList<EvalCaseResultRecord> Cases);

/// <summary>
/// Per-case result record written by <see cref="IEvalResultStore"/>.
/// </summary>
/// <remarks>
/// Carries two orthogonal axes: the quality axis (<see cref="Status"/>/<see cref="AssertionResults"/>) and
/// the mechanical axis (<see cref="MechanicalLevel"/>/<see cref="MechanicalFailureCount"/>/<see cref="MechanicalBreakdown"/>).
/// A case can have <c>Status=Pass</c> while <c>MechanicalLevel=Warning</c> — meaning the agent recovered from
/// tool errors or retries and still answered correctly. The three new fields default so existing call sites are unaffected.
/// </remarks>
public sealed record EvalCaseResultRecord(
    string EvalRunId,
    string CaseId,
    string? AgentRunId,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    EvalCaseStatus Status,
    string? ResponseText,
    IReadOnlyList<EvalAssertionResultRecord> AssertionResults,
    string? ProductionRunId = null,
    FailureLevel MechanicalLevel = FailureLevel.Default,
    int MechanicalFailureCount = 0,
    IReadOnlyDictionary<string, int>? MechanicalBreakdown = null);

/// <summary>Per-assertion result within a case.</summary>
public sealed record EvalAssertionResultRecord(
    int Index,
    string Kind,
    EvalAssertionStatus Status,
    double? Score,
    string? Reason);
