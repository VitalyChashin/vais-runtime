// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Runtime.CompilerServices;

namespace Vais.Agents.Hosting.Orleans;

/// <summary>
/// Client-side <see cref="IAgentJournal"/> adapter over <see cref="IAgentRunJournalGrain"/>.
/// Each <c>RunId</c> resolves to a grain of the same key; append and read calls forward
/// through Orleans RPC to the silo-owned durable state. Conversion between
/// <see cref="JournalEntry"/> (public, Orleans-free) and <see cref="JournalEntrySurrogate"/>
/// (the grain's wire type) happens at this boundary.
/// </summary>
/// <remarks>
/// <para>
/// Compose with <see cref="Core.StatefulAiAgent"/> via <see cref="Core.StatefulAgentOptions.Journal"/>
/// to run the turn-loop locally while the journal is durably owned by the silo grain.
/// The dispatcher's cache-replay path hits the grain once per dispatch to read the full
/// entry list — a one-round-trip cost per tool call.
/// </para>
/// <para>
/// <b>Threading.</b> Designed for use from non-grain contexts. Blocking on a grain call
/// from inside another grain's turn would deadlock the single-threaded grain scheduler;
/// in those contexts use <see cref="IAgentRunJournalGrain"/> directly.
/// </para>
/// </remarks>
public sealed class OrleansAgentJournal : IAgentJournal
{
    private readonly IGrainFactory _grainFactory;

    /// <summary>Create a journal proxy bound to a grain factory. Typically called via <see cref="OrleansAgentRuntime.GetJournal"/>.</summary>
    public OrleansAgentJournal(IGrainFactory grainFactory)
    {
        ArgumentNullException.ThrowIfNull(grainFactory);
        _grainFactory = grainFactory;
    }

    /// <inheritdoc />
    public async ValueTask AppendAsync(JournalEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.RunId);
        var grain = _grainFactory.GetGrain<IAgentRunJournalGrain>(entry.RunId);
        var surrogate = JournalEntrySurrogateHelpers.ToSurrogate(entry);
        await grain.AppendAsync(surrogate).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<JournalEntry> ReadAsync(
        string runId,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        var grain = _grainFactory.GetGrain<IAgentRunJournalGrain>(runId);
        var surrogates = await grain.GetEntriesAsync().ConfigureAwait(false);
        foreach (var surrogate in surrogates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return JournalEntrySurrogateHelpers.FromSurrogate(surrogate);
        }
    }

    /// <inheritdoc />
    public async ValueTask ClearAsync(string runId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        var grain = _grainFactory.GetGrain<IAgentRunJournalGrain>(runId);
        await grain.ClearAsync().ConfigureAwait(false);
    }
}
