// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;

namespace Vais.Agents;

/// <summary>
/// Declarative specification for an evaluation suite. Contains a named list of test
/// cases that the eval runner executes against registered agents or graphs. Loaded via
/// <c>vais apply -f suite.yaml</c>; stored in Orleans grain state (E1) or Postgres (E2+).
/// </summary>
/// <param name="Id">Stable identifier. Unique within the registry namespace.</param>
/// <param name="Version">Immutable version tag.</param>
/// <param name="Description">Human-readable description for registries / UIs.</param>
/// <param name="Labels">Arbitrary key/value metadata for filtering + organizing.</param>
public sealed record EvalSuiteManifest(
    string Id,
    string Version,
    string? Description = null,
    IReadOnlyDictionary<string, string>? Labels = null)
{
    /// <summary>Suite specification: target agent/graph + test cases.</summary>
    public required EvalSuiteSpec Spec { get; init; }
}

/// <summary>Runtime specification for an evaluation suite.</summary>
public sealed record EvalSuiteSpec
{
    /// <summary>Optional id of the target agent to invoke for each case. Mutually exclusive with <see cref="GraphId"/>.</summary>
    public string? AgentId { get; init; }

    /// <summary>Optional id of the target graph to invoke for each case. Mutually exclusive with <see cref="AgentId"/>.</summary>
    public string? GraphId { get; init; }

    /// <summary>Structured target reference. When present, takes precedence over <see cref="AgentId"/> / <see cref="GraphId"/>.</summary>
    public EvalTarget? Target { get; init; }

    /// <summary>Suite-level defaults applied to every case unless overridden.</summary>
    public EvalDefaults? Defaults { get; init; }

    /// <summary>Baseline run to diff against in <c>vais eval diff</c>.</summary>
    public EvalBaseline? Baseline { get; init; }

    /// <summary>Test cases that make up this suite.</summary>
    public required IReadOnlyList<EvalCase> Cases { get; init; }

    /// <summary>Suite-level replay mode default. Per-case <see cref="EvalCase.Replay"/> overrides this. Default: Live.</summary>
    public EvalReplayMode ReplayMode { get; init; } = EvalReplayMode.Live;
}

/// <summary>Structured target reference for an eval suite, including optional version pinning.</summary>
public sealed record EvalTarget
{
    /// <summary>Agent id to invoke. Mutually exclusive with <see cref="GraphRef"/>.</summary>
    public string? AgentRef { get; init; }

    /// <summary>Graph id to invoke. Mutually exclusive with <see cref="AgentRef"/>.</summary>
    public string? GraphRef { get; init; }

    /// <summary>Specific agent manifest version to pin. Snapshotted into <c>eval_runs.target_version</c>.</summary>
    public string? AgentVersion { get; init; }
}

/// <summary>Suite-level defaults applied to every case unless overridden per-assertion.</summary>
public sealed record EvalDefaults
{
    /// <summary>Model router alias used by <c>JudgeScoreAssertion</c>. Snapshotted into <c>eval_runs.judge_model</c>.</summary>
    public string? JudgeModel { get; init; }

    /// <summary>Per-case timeout. Null means no suite-level timeout.</summary>
    public TimeSpan? Timeout { get; init; }
}

/// <summary>Identifies a prior run to use as the comparison baseline for <c>vais eval diff</c>.</summary>
/// <param name="RunId">The eval run id of the baseline.</param>
public sealed record EvalBaseline(string RunId);

/// <summary>A single test case within an evaluation suite.</summary>
public sealed record EvalCase
{
    /// <summary>Stable case identifier, unique within the suite.</summary>
    public required string Id { get; init; }

    /// <summary>Human-readable case name.</summary>
    public string? Name { get; init; }

    /// <summary>Human-readable case description.</summary>
    public string? Description { get; init; }

    /// <summary>User-turn input text sent to the agent or graph.</summary>
    public required string Input { get; init; }

    /// <summary>Optional structured variables for graph invocations (key = state field name).</summary>
    public IReadOnlyDictionary<string, JsonElement>? Variables { get; init; }

    /// <summary>Optional expected output text for exact-match assertions.</summary>
    public string? ExpectedOutput { get; init; }

    /// <summary>Optional conversation history injected before the input turn.</summary>
    public IReadOnlyList<EvalHistoryTurn>? InitialHistory { get; init; }

    /// <summary>Per-case replay mode override. Null means use suite-level <see cref="EvalSuiteSpec.ReplayMode"/>.</summary>
    public EvalReplayMode? Replay { get; init; }

    /// <summary>Assertion list evaluated against the agent/graph response. Empty list means "no assertions, record only".</summary>
    public IReadOnlyList<EvalAssertion> Assertions { get; init; } = Array.Empty<EvalAssertion>();
}

/// <summary>A single turn in the initial conversation history injected before the case input.</summary>
/// <param name="Role">Role label — "user", "assistant", or "system".</param>
/// <param name="Content">Turn content text.</param>
public sealed record EvalHistoryTurn(string Role, string Content);

/// <summary>A single assertion evaluated against an agent/graph response.</summary>
/// <param name="Kind">Assertion kind string, matched against registered <c>IEvalAssertionKindRegistry</c> entries.</param>
/// <param name="Params">Optional JSON parameters for the assertion implementation.</param>
public sealed record EvalAssertion(string Kind, JsonElement? Params = null);

/// <summary>Controls whether the eval runner uses live LLM calls or cached recordings.</summary>
public enum EvalReplayMode
{
    /// <summary>Live mode — all tool/LLM calls execute end-to-end. Default.</summary>
    Live = 0,

    /// <summary>Cached mode — reuse previously recorded LLM responses from the journal.</summary>
    Cached = 1,
}

/// <summary>Stable identity reference to a registered <see cref="EvalSuiteManifest"/>.</summary>
/// <param name="Id">Suite identifier.</param>
/// <param name="Version">Suite version.</param>
public sealed record EvalSuiteHandle(string Id, string Version);
