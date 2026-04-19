// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents;

/// <summary>
/// Append-only, run-scoped journal of durable steps. Shipped in v0.5 as the anchor
/// for the durable-execution pillar: the tool-call dispatcher appends a
/// <see cref="ToolCallRecorded"/> after every outcome, and on resume the same
/// dispatcher replays cached outcomes so in-flight runs can survive a crash or a
/// human-in-the-loop pause without re-invoking tools.
/// </summary>
/// <remarks>
/// <para>
/// The contract is deliberately minimal. <see cref="AppendAsync"/> is the only
/// write; <see cref="ReadAsync"/> returns entries for a given run in append order;
/// <see cref="ClearAsync"/> drops all entries for a finished or abandoned run so
/// backing stores don't grow unbounded. The journal is agent-neutral — a future
/// multi-agent orchestrator can reuse the same primitive without re-design.
/// </para>
/// <para>
/// Implementations are expected to be append-safe across concurrent writers on the
/// same run. The in-process default guarantees this via per-run locking; remote
/// implementations should surface the same ordering invariant.
/// </para>
/// <para>
/// Journal-append failures propagate; the journal is load-bearing for resume
/// correctness. Consumers that want swallow semantics should wrap the journal.
/// </para>
/// </remarks>
public interface IAgentJournal
{
    /// <summary>
    /// Append an entry to the journal. Completes only after the entry is durably
    /// recorded for persistent implementations.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="entry"/> is null.</exception>
    ValueTask AppendAsync(JournalEntry entry, CancellationToken cancellationToken = default);

    /// <summary>
    /// Read entries for <paramref name="runId"/> in append order. Returns an empty
    /// sequence when the run is unknown.
    /// </summary>
    IAsyncEnumerable<JournalEntry> ReadAsync(string runId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Remove all entries for <paramref name="runId"/>. Idempotent — clearing an
    /// unknown run is a no-op.
    /// </summary>
    ValueTask ClearAsync(string runId, CancellationToken cancellationToken = default);
}
