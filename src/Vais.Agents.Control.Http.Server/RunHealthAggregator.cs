// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Vais.Agents;
using Vais.Agents.Control;
using Vais.Agents.Observability.GatewayEventStore;
using Vais.Agents.Observability.McpGatewayEventStore;
using Vais.Agents.Observability.RunHealthStore;
using Vais.Agents.Observability.RunStore;

namespace Vais.Agents.Control.Http;

/// <summary>
/// Default <see cref="IRunHealthAggregator"/>: an on-demand read over the durable stores, keyed by
/// the root run id. It folds (1) the bus-sourced mechanical signals persisted by
/// <c>RunHealthSignalSubscriber</c> across the whole run tree, (2) failed MCP gateway calls
/// (invisible on the bus for plugin-mediated tool calls), (3) failed LLM gateway calls (only when
/// the bus carried no LLM signal — the plugin-path fallback, avoiding double-counting in-process
/// retries), (4) failed graph nodes, and (5) failed background sub-runs — into one
/// <see cref="RunHealth"/>.
/// </summary>
/// <remarks>
/// v1 keeps the rollup deliberately simple: gateway/MCP failures are marked
/// <see cref="FailureLevel.Warning"/> (the event store cannot prove a call was turn-fatal — a fatal
/// turn also emits a bus <c>TurnFailed</c> / a failed node, which carry <see cref="FailureLevel.Error"/>),
/// and background recursion is one level (direct children of the root). Worst-level is a max, so it is
/// correct regardless; counts may slightly over-count when a single failure is observed by more than
/// one source. Deeper background recursion and store-vs-bus de-duplication are documented follow-ups.
/// </remarks>
public sealed class RunHealthAggregator : IRunHealthAggregator
{
    private readonly IRunHealthStore _signals;
    private readonly IGatewayEventStore _gateway;
    private readonly IMcpGatewayEventStore _mcp;
    private readonly IRunStore _runs;
    private readonly IBackgroundAgentTracker _background;
    private readonly IFailureOntologyCatalog? _catalog;
    private readonly IFailureAttributionRegistry? _attributionRegistry;

    /// <summary>Creates the aggregator over the durable run-health, gateway, run, and background stores.</summary>
    public RunHealthAggregator(
        IRunHealthStore signals,
        IGatewayEventStore gateway,
        IMcpGatewayEventStore mcp,
        IRunStore runs,
        IBackgroundAgentTracker background,
        IFailureOntologyCatalog? catalog = null,
        IFailureAttributionRegistry? attributionRegistry = null)
    {
        _signals = signals;
        _gateway = gateway;
        _mcp = mcp;
        _runs = runs;
        _background = background;
        _catalog = catalog;
        _attributionRegistry = attributionRegistry;
    }

    /// <inheritdoc />
    public async Task<RunHealth> GetRunHealthAsync(string rootRunId, CancellationToken ct = default)
    {
        var signals = new List<RunHealthSignal>();

        // 1. Bus-sourced mechanical signals across the whole run tree (tool/turn/partial/retry/fallback/guardrail).
        //    For legacy rows where ConceptName was not yet stamped, fall back to the catalog.
        foreach (var s in await _signals.ListByRunTreeAsync(rootRunId, ct).ConfigureAwait(false))
        {
            signals.Add(s.ConceptName is null && _catalog is not null
                ? s with { ConceptName = _catalog.FromSignalKind(s.Kind)?.Name }
                : s);
        }

        // 2. MCP gateway failures — not on the bus for plugin-mediated tool calls.
        //    ListByRunAsync uses exact run_id match (not a tree prefix query), so events returned
        //    here are for the root run only. AttributionPath is tool-name based; artifact lookup
        //    can refine ConceptName and add MCP server context.
        foreach (var e in await _mcp.ListByRunAsync(rootRunId, ct: ct).ConfigureAwait(false))
        {
            if (!string.IsNullOrEmpty(e.ErrorType))
            {
                var mcpConceptName = _catalog?.FromSignalKind(RunHealthSignalKind.McpError)?.Name;
                var mcpAttributionPath = BuildMcpAttributionPath(e.ToolName, _attributionRegistry, ref mcpConceptName);
                signals.Add(new RunHealthSignal(e.ToolName, RunHealthSignalKind.McpError,
                    FailureLevel.Warning, e.ErrorType, IsTransient: false, e.At,
                    ConceptName: mcpConceptName,
                    AttributionPath: mcpAttributionPath));
            }
        }

        // 3. LLM gateway failures — only when the bus carried no LLM signal (plugin path), to avoid
        //    double-counting an in-process retry/fallback the bus already recorded richly.
        var hasBusLlm = signals.Exists(s => s.Kind is RunHealthSignalKind.LlmRetry or RunHealthSignalKind.LlmFallback);
        if (!hasBusLlm)
        {
            foreach (var e in await _gateway.ListByRunAsync(rootRunId, ct: ct).ConfigureAwait(false))
            {
                if (!string.IsNullOrEmpty(e.ErrorType))
                {
                    var modelSource = e.ModelId ?? "llm-gateway";
                    signals.Add(new RunHealthSignal(modelSource, RunHealthSignalKind.LlmError,
                        FailureLevel.Warning, e.ErrorType, IsTransient: false, e.At,
                        ConceptName: _catalog?.FromSignalKind(RunHealthSignalKind.LlmError)?.Name,
                        AttributionPath: modelSource));
                }
            }
        }

        // 4. Failed graph nodes.
        var nodes = await _runs.GetNodesAsync(rootRunId, ct).ConfigureAwait(false);
        foreach (var n in nodes)
        {
            if (n.Status == RunStatus.Failed)
            {
                var nodeSource = n.AgentId ?? n.NodeId;
                signals.Add(new RunHealthSignal(nodeSource, RunHealthSignalKind.NodeFailed,
                    FailureLevel.Error, n.Error, IsTransient: false, n.StartedAt,
                    ConceptName: _catalog?.FromSignalKind(RunHealthSignalKind.NodeFailed)?.Name,
                    AttributionPath: nodeSource));
            }
        }

        // 5. Failed background sub-runs launched by this run.
        var background = new List<RunHealthSignal>();
        foreach (var b in await _background.ListAsync(rootRunId, ct).ConfigureAwait(false))
        {
            if (b.Status == BackgroundAgentRunStatus.Failed)
            {
                background.Add(new RunHealthSignal(b.ChildAgentId, RunHealthSignalKind.TurnFailed,
                    FailureLevel.Error, b.Error, IsTransient: false, b.StartedAt,
                    ConceptName: _catalog?.FromSignalKind(RunHealthSignalKind.TurnFailed)?.Name,
                    AttributionPath: b.ChildAgentId));
            }
        }

        var worst = RunHealthLevel.Healthy;
        foreach (var s in signals.Concat(background))
        {
            var lvl = RunHealth.ToRunHealthLevel(s.Level);
            if (lvl > worst)
            {
                worst = lvl;
            }
        }

        return new RunHealth(rootRunId, worst, signals, background);
    }

    /// <summary>
    /// Attempts to extract the agent name from the sub-run ID encoding convention
    /// <c>{parentRunId}__{agentName}__{hash}</c>. Returns null for root runs or unknown shapes.
    /// Only applicable to bus-sourced signals; MCP gateway store uses exact run_id match.
    /// </summary>
    internal static string? TryExtractAgentFromRunId(string? runId)
    {
        if (string.IsNullOrEmpty(runId)) return null;
        var parts = runId.Split("__", StringSplitOptions.None);
        return parts.Length >= 3 ? parts[^2] : null;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RunHealthListItem>> ListDegradedRunsAsync(
        FailureLevel minLevel = FailureLevel.Warning,
        DateTimeOffset? since = null,
        int limit = 50,
        CancellationToken ct = default)
    {
        var clampedLimit = Math.Clamp(limit, 1, 200);
        var rows = await _signals.ListDegradedRunsAsync(minLevel, since, clampedLimit, ct).ConfigureAwait(false);
        return rows
            .Select(r => new RunHealthListItem(
                RunId: r.RunId,
                Level: RunHealth.ToRunHealthLevel(r.WorstLevel).ToString().ToLowerInvariant(),
                SignalCount: r.SignalCount,
                LatestAt: r.LatestAt))
            .ToList();
    }

    /// <summary>
    /// Builds an <c>AttributionPath</c> for an MCP-error signal and optionally refines
    /// <paramref name="conceptName"/> from the first artifact that has a per-tool annotation.
    /// MCP query is exact-run-id (no agent context); path is tool-name based with optional
    /// MCP server from the artifact.
    /// </summary>
    internal static string BuildMcpAttributionPath(
        string toolName,
        IFailureAttributionRegistry? registry,
        ref string? conceptName)
    {
        // Scan all registered artifacts for a tool annotation matching this tool.
        // O(N artifacts) but N is small (deployment-local, bounded).
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
