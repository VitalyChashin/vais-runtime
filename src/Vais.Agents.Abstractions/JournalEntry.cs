// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;

namespace Vais.Agents;

/// <summary>
/// A single durable step recorded by an <see cref="IAgentJournal"/> during a run.
/// Entries are appended in execution order and replayed in order on resume; the
/// runtime uses them to skip work that already happened (e.g. a tool call) and
/// continue the loop where it left off.
/// </summary>
/// <remarks>
/// <para>
/// Closed hierarchy. In v0.5 the only subclass is <see cref="ToolCallRecorded"/> —
/// the tool-call-only journal is the MVP granularity for the durable-execution
/// pillar. Future subclasses (e.g. a provider-turn record) ship as unshipped
/// additions; consumers that pattern-match exhaustively need updates when new
/// subclasses land.
/// </para>
/// </remarks>
/// <param name="RunId">Run this entry belongs to; scopes the journal's read path.</param>
/// <param name="At">UTC timestamp when the entry was appended.</param>
public abstract record JournalEntry(string RunId, DateTimeOffset At);

/// <summary>
/// A tool-call step recorded after the dispatcher ran a tool and obtained an
/// outcome. On resume, the dispatcher skips re-invoking tools whose
/// (<see cref="JournalEntry.RunId"/>, <see cref="CallId"/>) pair is already recorded
/// and returns the cached <see cref="Outcome"/> directly.
/// </summary>
/// <param name="RunId">Run this entry belongs to.</param>
/// <param name="CallId">Provider-assigned correlation id carried from the originating <see cref="ToolCallRequest"/>.</param>
/// <param name="ToolName">Name of the tool that was invoked.</param>
/// <param name="Arguments">Arguments the model supplied to the tool. Preserved so auditors can inspect the call without relying on event-bus retention.</param>
/// <param name="Outcome">Captured <see cref="ToolCallOutcome"/>. Returned verbatim on resume — tools are not re-invoked.</param>
/// <param name="At">UTC timestamp when the tool call resolved.</param>
public sealed record ToolCallRecorded(
    string RunId,
    string CallId,
    string ToolName,
    JsonElement Arguments,
    ToolCallOutcome Outcome,
    DateTimeOffset At)
    : JournalEntry(RunId, At);

/// <summary>
/// A completion delta (text fragment, metadata, or tool calls) yielded to the
/// streaming consumer during a run. On full replay when <see cref="Vais.Agents.ReplayMode.Full"/>
/// is enabled, deltas are re-yielded verbatim without re-invoking the provider.
/// </summary>
/// <remarks>
/// <para>
/// Deltas are journaled only when the agent's replay mode is
/// <see cref="Vais.Agents.ReplayMode.Full"/>. In the default <see cref="Vais.Agents.ReplayMode.ToolOnly"/> mode,
/// deltas are not journaled and the provider is re-invoked on resume.
/// </para>
/// <para>
/// The <see cref="SequenceNumber"/> provides ordering guarantees and enables future
/// per-delta resume (resuming from an arbitrary delta index). In v0.21, replay always
/// starts from the beginning of a resumed run; per-delta resume is a future enhancement.
/// </para>
/// </remarks>
/// <param name="RunId">Run this entry belongs to.</param>
/// <param name="SequenceNumber">Monotonic delta index within the run (0, 1, 2, ...).</param>
/// <param name="Delta">The raw completion update from the provider.</param>
/// <param name="At">UTC timestamp when the delta was yielded.</param>
public sealed record CompletionDeltaRecorded(
    string RunId,
    int SequenceNumber,
    CompletionUpdate Delta,
    DateTimeOffset At)
    : JournalEntry(RunId, At);
