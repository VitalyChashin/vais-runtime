// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Control;

/// <summary>
/// In-process counter populated by <c>OrleansOutgoingActivityFilter</c>.
/// Tracks whether <see cref="System.Diagnostics.Activity.Current"/> was non-null
/// at the time each outgoing grain call was dispatched, keyed by grain interface name.
/// Surfaced by <c>GET /v1/diagnostics/filter-status</c> and <c>vais diagnose filter-status</c>.
/// </summary>
public interface IFilterStatusTracker
{
    /// <summary>Record one outgoing grain call for <paramref name="grainInterface"/>.</summary>
    void RecordCall(string grainInterface, bool hasActivity);

    /// <summary>Return a snapshot of per-interface call counts, ordered by total calls descending.</summary>
    IReadOnlyList<FilterCallEntry> GetSnapshot();
}

/// <summary>Per-grain-interface call counts from <see cref="IFilterStatusTracker"/>.</summary>
/// <param name="GrainInterface">Fully-qualified grain interface name.</param>
/// <param name="WithActivity">Calls where <see cref="System.Diagnostics.Activity.Current"/> was non-null.</param>
/// <param name="WithoutActivity">Calls where <see cref="System.Diagnostics.Activity.Current"/> was null.</param>
public sealed record FilterCallEntry(
    string GrainInterface,
    long WithActivity,
    long WithoutActivity);
