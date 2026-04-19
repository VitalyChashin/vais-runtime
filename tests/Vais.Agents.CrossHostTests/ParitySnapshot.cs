// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.CrossHostTests;

/// <summary>
/// Deterministic, host-agnostic snapshot of what a scenario produced on one host.
/// Fields intentionally exclude timings, start timestamps, and cluster-local ids
/// so two runs on different hosts can be compared byte-for-byte once the
/// <em>logic</em> matches — the whole point of this test project.
/// </summary>
public sealed record ParitySnapshot(
    IReadOnlyList<string> History,
    IReadOnlyList<string> FilterInvocations,
    IReadOnlyList<string> UsageSummaries)
{
    /// <summary>
    /// Flatten a chat history into a <c>role|text</c> list. Matches what
    /// <c>IAiAgent.History</c> exposes but strips everything else the snapshot does
    /// not care about (nothing right now — <see cref="ChatTurn"/> has only role + text).
    /// </summary>
    public static IReadOnlyList<string> SummariseHistory(IReadOnlyList<ChatTurn> turns)
        => turns.Select(t => $"{t.Role}|{t.Text}").ToArray();

    /// <summary>
    /// Flatten a <see cref="UsageRecord"/> into a host-agnostic string. Excludes
    /// <c>Duration</c> and <c>StartedAt</c> (non-deterministic), and <c>AgentName</c>
    /// (so scenarios that vary the agent id per host still compare).
    /// </summary>
    public static string SummariseUsage(UsageRecord r) =>
        $"provider={r.ProviderName}," +
        $"model={r.ModelId}," +
        $"prompt={FormatNullable(r.PromptTokens)}," +
        $"completion={FormatNullable(r.CompletionTokens)}," +
        $"succeeded={r.Succeeded}," +
        $"error={r.ErrorType ?? "<null>"}";

    private static string FormatNullable(int? value) => value?.ToString() ?? "<null>";
}
