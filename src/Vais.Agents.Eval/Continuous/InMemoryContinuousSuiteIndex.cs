// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Eval.Continuous;

/// <summary>
/// <see cref="IContinuousSuiteIndex"/> implementation. Dev-only state — rebuilt from
/// the durable registry on silo restart via <c>ContinuousSuiteActivator</c>.
/// </summary>
public sealed class InMemoryContinuousSuiteIndex : IContinuousSuiteIndex
{
    // keyed by suiteId; replaced atomically on Refresh
    private volatile IReadOnlyList<ContinuousSuiteEntry> _entries = Array.Empty<ContinuousSuiteEntry>();

    /// <inheritdoc/>
    public IEnumerable<ContinuousSuiteEntry> SuitesFor(string workspaceId, string? agentRef, string? graphRef)
    {
        var snapshot = _entries;
        foreach (var entry in snapshot)
        {
            // workspace filter: empty means "all workspaces"
            if (!string.IsNullOrEmpty(entry.WorkspaceId) &&
                !string.Equals(entry.WorkspaceId, workspaceId, StringComparison.OrdinalIgnoreCase))
                continue;

            if (agentRef is not null && entry.AgentRef is not null &&
                string.Equals(entry.AgentRef, agentRef, StringComparison.OrdinalIgnoreCase))
            {
                yield return entry;
            }
            else if (graphRef is not null && entry.GraphRef is not null &&
                     string.Equals(entry.GraphRef, graphRef, StringComparison.OrdinalIgnoreCase))
            {
                yield return entry;
            }
        }
    }

    /// <inheritdoc/>
    public void Refresh(IReadOnlyList<EvalSuiteManifest> activeSuites)
    {
        ArgumentNullException.ThrowIfNull(activeSuites);
        var entries = new List<ContinuousSuiteEntry>();
        foreach (var suite in activeSuites)
        {
            if (suite.Spec.Sampling is null) continue;
            var agentRef = suite.Spec.Target?.AgentRef ?? suite.Spec.AgentId;
            var graphRef = suite.Spec.Target?.GraphRef ?? suite.Spec.GraphId;
            var workspaceId = suite.Labels?.TryGetValue("workspace", out var ws) == true ? ws : string.Empty;
            entries.Add(new ContinuousSuiteEntry(
                suite.Id, workspaceId, agentRef, graphRef, suite.Spec.Sampling.Rate));
        }
        _entries = entries;
    }
}
