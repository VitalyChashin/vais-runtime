// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Hosting.Orleans;

/// <summary>
/// Orleans-side surface for a per-run durable-execution journal. Each grain instance
/// holds the append-only list of journal entries for one run.
/// </summary>
/// <remarks>
/// <para>
/// <b>Grain key.</b> <see cref="IGrainWithStringKey"/>. The key is the opaque
/// <c>RunId</c> stamped by <c>StatefulAiAgent</c> on <see cref="AgentContext.RunId"/>
/// — globally unique, no encoding needed.
/// </para>
/// <para>
/// <b>Wire type.</b> The grain carries <see cref="JournalEntrySurrogate"/> on the wire
/// rather than <see cref="JournalEntry"/> directly. Orleans prefers concrete
/// <c>[GenerateSerializer]</c> types over surrogate-dispatched polymorphic abstract
/// types across grain RPC boundaries; the surrogate keeps the wire shape stable and
/// leaves <c>JournalEntry</c> -&gt; <c>JournalEntrySurrogate</c> conversion at the
/// <see cref="OrleansAgentJournal"/> boundary.
/// </para>
/// <para>
/// <b>Single-writer guarantee.</b> Orleans serialises calls per grain, which means
/// per run here. The dispatcher's cache-replay path reads the full entry list in one
/// RPC and scans client-side; append happens once per tool-call outcome.
/// </para>
/// </remarks>
public interface IAgentRunJournalGrain : IGrainWithStringKey
{
    /// <summary>Append a single entry to this run's durable journal.</summary>
    Task AppendAsync(JournalEntrySurrogate entry);

    /// <summary>Snapshot of the journal's entries, in append order.</summary>
    Task<IReadOnlyList<JournalEntrySurrogate>> GetEntriesAsync();

    /// <summary>Clear the journal's entries and deactivate on idle.</summary>
    Task ClearAsync();
}
