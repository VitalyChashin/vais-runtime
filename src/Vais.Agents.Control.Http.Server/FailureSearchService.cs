// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Vais.Agents.Control;
using Vais.Agents.Observability.McpGatewayEventStore;
using Vais.Agents.Observability.RunHealthStore;

namespace Vais.Agents.Control.Http;

/// <summary>
/// Default <see cref="IFailureSearchService"/>: cross-run failure search that fans out to the
/// store best suited to each concept and merges the results.
/// <list type="bullet">
///   <item>Bus-sourced concepts (ToolError, TurnFailed, PluginPartial, LlmCallRetried,
///   LlmFallbackEngaged, GuardrailTriggered) → <see cref="IRunHealthStore.QuerySignalsAsync"/>.</item>
///   <item><c>McpToolError</c> (the headline cross-run case for plugin-mediated tool failures) →
///   <see cref="IMcpGatewayEventStore.QueryFailedAcrossGatewaysAsync"/>; concept and attribution
///   are re-derived from the failure ontology catalog + attribution registry the same way the
///   per-run aggregator does for that store.</item>
///   <item>NodeFailed / LlmCallFailure (plugin path) / background failures are NOT indexed
///   cross-run in v1. They surface only per-run via <see cref="IRunHealthAggregator.GetRunHealthAsync"/>.</item>
/// </list>
/// </summary>
public sealed class FailureSearchService : IFailureSearchService
{
    private readonly IRunHealthStore _runHealth;
    private readonly IMcpGatewayEventStore _mcp;
    private readonly IFailureOntologyCatalog? _catalog;
    private readonly IFailureAttributionRegistry? _attributionRegistry;

    /// <summary>Creates the service over the run-health and MCP gateway event stores.</summary>
    public FailureSearchService(
        IRunHealthStore runHealth,
        IMcpGatewayEventStore mcp,
        IFailureOntologyCatalog? catalog = null,
        IFailureAttributionRegistry? attributionRegistry = null)
    {
        _runHealth = runHealth;
        _mcp = mcp;
        _catalog = catalog;
        _attributionRegistry = attributionRegistry;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<FailureSearchResult>> SearchAsync(
        FailureSearchQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var since = query.Since ?? DateTimeOffset.UtcNow - TimeSpan.FromHours(24);
        var limit = Math.Clamp(query.Limit, 1, 200);
        var results = new List<FailureSearchResult>();

        // Decide which stores to hit based on the concept filter. McpToolError lives in the MCP
        // gateway event store; all other indexed concepts live in the run-health store. A null
        // concept filter fans out to both.
        var wantsMcp = query.ConceptName is null
            || string.Equals(query.ConceptName, "McpToolError", StringComparison.Ordinal)
            || IsMcpToolErrorDescendant(query.ConceptName);
        var wantsBus = query.ConceptName is null
            || !string.Equals(query.ConceptName, "McpToolError", StringComparison.Ordinal);

        // ── Bus-sourced (run-health store) ────────────────────────────────────
        // The store applies exact concept_name match on its filter; to honour Part 2a's
        // parent-walk promise (querying "ToolError" finds "ToolError/AnyChild"), fetch with
        // no concept filter, then post-filter via IFailureOntologyCatalog.IsMatchOrDescendant —
        // identical discipline to the MCP branch below.
        if (wantsBus)
        {
            var rows = await _runHealth.QuerySignalsAsync(
                conceptName: null,
                agentName: query.AgentName,
                since: since,
                limit: limit,
                ct: ct).ConfigureAwait(false);
            foreach (var r in rows)
            {
                var concept = r.ConceptName ?? _catalog?.FromSignalKind(r.Kind)?.Name ?? r.Kind.ToString();
                if (!string.IsNullOrEmpty(query.ConceptName)
                    && !ConceptMatches(concept, query.ConceptName))
                    continue;
                results.Add(new FailureSearchResult(
                    RunId: r.RunId,
                    ConceptName: concept,
                    AttributionPath: r.AttributionPath,
                    Source: r.Source,
                    Level: r.Level.ToString().ToLowerInvariant(),
                    ErrorType: r.ErrorType,
                    At: r.At));
            }
        }

        // ── MCP gateway store (McpToolError concept family) ───────────────────
        if (wantsMcp)
        {
            var events = await _mcp.QueryFailedAcrossGatewaysAsync(
                toolName: null,
                since: since,
                limit: limit,
                ct: ct).ConfigureAwait(false);
            foreach (var e in events)
            {
                if (string.IsNullOrEmpty(e.RunId)) continue;
                if (!string.IsNullOrEmpty(query.AgentName)) continue; // MCP store has no agent column
                var concept = _catalog?.FromSignalKind(RunHealthSignalKind.McpError)?.Name ?? "McpToolError";
                var path = BuildMcpAttributionPath(e.ToolName, _attributionRegistry, ref concept);

                // Apply concept-filter post-hoc: if the caller asked for a specific concept and
                // the artifact-refined concept does NOT match (or descend from) it, skip the row.
                if (!string.IsNullOrEmpty(query.ConceptName)
                    && !ConceptMatches(concept, query.ConceptName))
                    continue;

                results.Add(new FailureSearchResult(
                    RunId: e.RunId,
                    ConceptName: concept,
                    AttributionPath: path,
                    Source: e.ToolName,
                    Level: "warning",
                    ErrorType: e.ErrorType,
                    At: e.At));
            }
        }

        return results
            .OrderByDescending(r => r.At)
            .Take(limit)
            .ToList();
    }

    private bool IsMcpToolErrorDescendant(string conceptName) =>
        ConceptMatches(conceptName, "McpToolError");

    /// <summary>
    /// Concept-match with two fallbacks: (1) catalog's <c>IsMatchOrDescendant</c> (cheap;
    /// works when both names are registered), then (2) slash-path convention — if the
    /// candidate has the form <c>"Parent/Child"</c> and the parent equals (or descends
    /// from) the filter, treat the candidate as a descendant. The slash path is the
    /// documented convention for artifact-supplied sub-concepts that may NOT also be
    /// registered in an overlay; without this fallback, an artifact concept like
    /// <c>"McpToolError/AuthExpired"</c> wouldn't match a parent-concept query, even
    /// though the operator's intent is clearly to attach it under that parent.
    /// </summary>
    private bool ConceptMatches(string candidate, string filter)
    {
        if (string.Equals(candidate, filter, StringComparison.Ordinal))
            return true;
        if (_catalog is not null && _catalog.IsMatchOrDescendant(candidate, filter))
            return true;
        var slashIdx = candidate.IndexOf('/', StringComparison.Ordinal);
        if (slashIdx > 0)
        {
            var parent = candidate[..slashIdx];
            // Recurse on the parent — handles multi-level (X/Y/Z) and re-uses the catalog walk.
            return ConceptMatches(parent, filter);
        }
        return false;
    }

    /// <summary>
    /// Mirrors <see cref="RunHealthAggregator.BuildMcpAttributionPath"/> — re-derives the
    /// concept/path projection from the attribution registry for an MCP event. Kept inline to
    /// avoid the cross-class coupling; refactor to share if a third caller emerges.
    /// </summary>
    private static string BuildMcpAttributionPath(
        string toolName,
        IFailureAttributionRegistry? registry,
        ref string conceptName)
    {
        if (registry is not null)
        {
            foreach (var refName in registry.Names)
            {
                var artifact = registry.Get(refName);
                var annotation = artifact?.ForTool(toolName);
                if (annotation is null) continue;

                if (!string.IsNullOrEmpty(annotation.Concept))
                    conceptName = annotation.Concept;

                var mcpServerId = annotation.McpServerId;
                return string.IsNullOrEmpty(mcpServerId) ? toolName : $"{mcpServerId}/{toolName}";
            }
        }
        return toolName;
    }
}
