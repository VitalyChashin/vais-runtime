// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Control;

/// <summary>
/// Cross-run failure search — powers <c>vais.failures</c> on the diagnostic MCP surface and the
/// matching REST endpoint. The implementation in <c>Vais.Agents.Control.Http.Server</c> queries
/// the appropriate underlying store(s) per concept (bus-sourced concepts → run-health store;
/// <c>McpToolError</c> → MCP gateway event store) and re-derives concept/attribution from the
/// shared catalog + attribution registry the same way the per-run aggregator does.
/// </summary>
/// <remarks>
/// The interface lives in <c>Control.Abstractions</c> so <c>Vais.Agents.Control.Mcp.Server</c>
/// can resolve it from DI without taking a project-reference on <c>Http.Server</c> — the same
/// pattern <see cref="IRunHealthAggregator"/> uses.
/// </remarks>
public interface IFailureSearchService
{
    /// <summary>
    /// Searches for failure signals matching <paramref name="query"/>. Returns the most recent
    /// matches up to <see cref="FailureSearchQuery.Limit"/>, sorted by timestamp descending.
    /// </summary>
    Task<IReadOnlyList<FailureSearchResult>> SearchAsync(FailureSearchQuery query, CancellationToken ct = default);
}

/// <summary>Filter parameters for <see cref="IFailureSearchService.SearchAsync"/>.</summary>
/// <param name="ConceptName">Failure concept name (parent-walk via <c>IsMatchOrDescendant</c>). Null returns all indexed concepts.</param>
/// <param name="AgentName">Source-agent filter. Null returns any source.</param>
/// <param name="Since">Earliest timestamp to include. Null defaults to 24 hours before now.</param>
/// <param name="Limit">Maximum results to return (default 50, max 200).</param>
public sealed record FailureSearchQuery(
    string? ConceptName = null,
    string? AgentName = null,
    DateTimeOffset? Since = null,
    int Limit = 50);

/// <summary>One row returned by <see cref="IFailureSearchService.SearchAsync"/>.</summary>
/// <param name="RunId">The run that produced the signal.</param>
/// <param name="ConceptName">Resolved failure concept (catalog or artifact-refined).</param>
/// <param name="AttributionPath">Deployment-grounded attribution path; null when no artifact / no agent context.</param>
/// <param name="Source">The signal's <c>Source</c> field — agent name, tool name, or node id.</param>
/// <param name="Level">Severity: <c>warning</c> (recovered) or <c>error</c> (fatal).</param>
/// <param name="ErrorType">Error type from the originating store; null when not captured.</param>
/// <param name="At">UTC timestamp of the signal.</param>
public sealed record FailureSearchResult(
    string RunId,
    string ConceptName,
    string? AttributionPath,
    string Source,
    string Level,
    string? ErrorType,
    DateTimeOffset At);
