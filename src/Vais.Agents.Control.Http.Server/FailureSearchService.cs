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
        if (wantsBus)
        {
            // For descendant concepts (e.g. "ToolError/SomeChild"), exact match on the store row.
            // The subscriber stamps either the base concept (ToolError) or an artifact override.
            var rows = await _runHealth.QuerySignalsAsync(
                conceptName: query.ConceptName,
                agentName: query.AgentName,
                since: since,
                limit: limit,
                ct: ct).ConfigureAwait(false);
            foreach (var r in rows)
            {
                results.Add(new FailureSearchResult(
                    RunId: r.RunId,
                    ConceptName: r.ConceptName ?? _catalog?.FromSignalKind(r.Kind)?.Name ?? r.Kind.ToString(),
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
        _catalog?.IsMatchOrDescendant(conceptName, "McpToolError") ?? false;

    private bool ConceptMatches(string candidate, string filter) =>
        _catalog?.IsMatchOrDescendant(candidate, filter)
        ?? string.Equals(candidate, filter, StringComparison.Ordinal);

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
