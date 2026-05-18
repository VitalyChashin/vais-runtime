// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Eval.Continuous;

/// <summary>
/// In-process index of active continuous eval suites keyed by workspace + target ref.
/// Derived from <see cref="IEvalSuiteRegistry"/> at startup and refreshed when suites
/// change. Thread-safe singleton; in-memory state is rebuilt from durable registry on
/// silo restart (see <c>ContinuousSuiteActivator</c>).
/// </summary>
public interface IContinuousSuiteIndex
{
    /// <summary>
    /// Enumerate suites whose target matches the supplied workspace and
    /// <paramref name="agentRef"/> or <paramref name="graphRef"/>.
    /// </summary>
    IEnumerable<ContinuousSuiteEntry> SuitesFor(string workspaceId, string? agentRef, string? graphRef);

    /// <summary>Replace the full index contents from the latest registry snapshot.</summary>
    void Refresh(IReadOnlyList<EvalSuiteManifest> activeSuites);
}

/// <summary>Minimal descriptor cached in <see cref="IContinuousSuiteIndex"/> for matching incoming runs.</summary>
/// <param name="SuiteId">Suite id.</param>
/// <param name="WorkspaceId">Workspace scope. Empty string means "match any workspace".</param>
/// <param name="AgentRef">Agent id to match; null if this suite targets a graph.</param>
/// <param name="GraphRef">Graph id to match; null if this suite targets an agent.</param>
/// <param name="SamplingRate">Probability in [0,1] that a given run is sampled.</param>
public sealed record ContinuousSuiteEntry(
    string SuiteId,
    string WorkspaceId,
    string? AgentRef,
    string? GraphRef,
    double SamplingRate);
