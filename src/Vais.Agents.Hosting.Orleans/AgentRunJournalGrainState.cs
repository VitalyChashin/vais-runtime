// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Hosting.Orleans;

/// <summary>
/// Persisted state for <see cref="AgentRunJournalGrain"/>: the append-only list of
/// <see cref="JournalEntrySurrogate"/> entries for a single run. The surrogate is
/// <c>[GenerateSerializer]</c>-annotated, so the list serialises without touching
/// the polymorphic <see cref="JournalEntry"/> dispatch path.
/// </summary>
[GenerateSerializer]
public sealed class AgentRunJournalGrainState
{
    /// <summary>Append-only list of journal entries, in the order they were appended.</summary>
    [Id(0)]
    public List<JournalEntrySurrogate> Entries { get; set; } = new();
}
