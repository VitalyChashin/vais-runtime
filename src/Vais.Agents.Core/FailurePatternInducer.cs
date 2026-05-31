// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using Vais.Agents.Control;

namespace Vais.Agents.Core;

/// <summary>Tuning knobs for <see cref="FailurePatternInducer"/>.</summary>
public sealed record FailurePatternInducerOptions
{
    /// <summary>
    /// Minimum number of failure signals for a <c>(concept, attribution-path)</c> group before
    /// it becomes a proposal. Default 5. Set higher in high-traffic deployments where isolated
    /// failures are noise.
    /// </summary>
    public int MinSupport { get; init; } = 5;

    /// <summary>
    /// Window considered when applying the recency weight for proposals. Groups whose most-recent
    /// failure falls within this window are promoted; older groups are still reported but rank
    /// lower. Default 168 hours (1 week).
    /// </summary>
    public int RecencyWeightHours { get; init; } = 168;

    /// <summary>
    /// Failure count at or above which a prior is classified as <see cref="RecipeProposalRiskLevel.Medium"/>.
    /// Below this threshold, priors default to <see cref="RecipeProposalRiskLevel.Low"/> (advisory label).
    /// Default 10.
    /// </summary>
    public int MediumRiskMinFailureCount { get; init; } = 10;

    /// <summary>
    /// Maximum number of failure signals to fetch from the search service per induction run
    /// (hard-capped at 200 by <c>FailureSearchService</c>). Default 200.
    /// </summary>
    public int MaxFetchLimit { get; init; } = 200;
}

/// <summary>
/// Layer-3 (failure-pattern) induction over the failure-signal corpus — mines the run-health
/// store and MCP gateway event store, groups failures by <c>(concept, attribution-path)</c>,
/// and emits each qualifying group as a <see cref="RecipeProposalKind.FailurePrior"/> proposal.
/// </summary>
/// <remarks>
/// <para>
/// Data source: <see cref="IFailureSearchService.SearchAsync"/> (from
/// <c>Vais.Agents.Control.Abstractions</c>) fans out to:
/// (a) <c>IRunHealthStore.QuerySignalsAsync</c> for bus-sourced failures (ToolError, LlmRetry,
///     TurnFailed, PluginPartial, GuardrailTriggered) with pre-computed <c>AttributionPath</c>;
/// (b) <c>IMcpGatewayEventStore.QueryFailedAcrossGatewaysAsync</c> for MCP tool failures
///     with synthetic <c>{gatewayId}/{toolName}</c> attribution.
/// </para>
/// <para>
/// The simplified-mode caveat (research §11.5, user-acknowledged v1 tradeoff): because the
/// failure-signal corpus contains only failures, <see cref="RecipeProposal.Confidence"/> is
/// set to <c>1.0</c> rather than a true fail-rate. <see cref="FailurePatternInducerOptions.MinSupport"/>
/// is the operative quality gate; operators see <see cref="RecipeProposal.Support"/> (= FailureCount)
/// as the key metric in <c>vais recipes list</c>.
/// </para>
/// <para>
/// Coverage caveat: <c>FailureSearchService</c> caps results at 200 per induction run. In
/// high-volume deployments this means older or less-frequent patterns may be missed; widen
/// the induction window or reduce the <c>--since</c> scope to compensate.
/// </para>
/// </remarks>
public sealed class FailurePatternInducer(
    IFailureSearchService searchService,
    FailurePatternInducerOptions? options = null) : IRecipeInducer
{
    private static readonly JsonSerializerOptions BodySerializerOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IFailureSearchService _search = searchService ?? throw new ArgumentNullException(nameof(searchService));
    private readonly FailurePatternInducerOptions _options = options ?? new();

    /// <inheritdoc />
    public async Task<IReadOnlyList<RecipeProposal>> InduceAsync(
        TrajectoryQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        // Fan out to both failure stores via the shared search service.
        var searchQuery = new FailureSearchQuery(
            ConceptName: query.ConceptName,
            AgentName: query.AgentId,
            Since: query.Since,
            Limit: _options.MaxFetchLimit);

        var results = await _search.SearchAsync(searchQuery, cancellationToken).ConfigureAwait(false);
        if (results.Count == 0) return [];

        // Group by (ConceptName, AttributionPath). Both fields must be non-empty.
        var groups = new Dictionary<(string Concept, string Path), GroupAccumulator>(
            EqualityComparer<(string, string)>.Default);

        foreach (var r in results)
        {
            if (string.IsNullOrEmpty(r.ConceptName)) continue;
            var path = string.IsNullOrEmpty(r.AttributionPath) ? r.Source : r.AttributionPath;
            if (string.IsNullOrEmpty(path)) continue;

            var key = (r.ConceptName, path);
            if (!groups.TryGetValue(key, out var acc))
            {
                acc = new GroupAccumulator();
                groups[key] = acc;
            }
            acc.Add(r.At);
        }

        var now = DateTimeOffset.UtcNow;
        var proposals = new List<RecipeProposal>();

        foreach (var ((concept, path), acc) in groups)
        {
            if (acc.Count < _options.MinSupport) continue;

            var (agentName, toolName) = ParseAttributionPath(path);
            var body = new FailurePriorBody
            {
                AgentName = agentName,
                ConceptName = concept,
                AttributionPath = path,
                ToolName = toolName,
                FailureCount = acc.Count,
                FirstSeen = acc.FirstSeen,
                LastSeen = acc.LastSeen,
            };

            proposals.Add(new RecipeProposal
            {
                ProposalId = Guid.CreateVersion7().ToString("N"),
                Kind = RecipeProposalKind.FailurePrior,
                Concept = concept,
                Body = JsonSerializer.Serialize(body, BodySerializerOptions),
                Support = acc.Count,
                // Confidence is 0.0 — the corpus is failure-only so a true fail-rate is
                // unavailable (v1 simplification); 0 signals "not computed" rather than
                // the actively-misleading 1.0 ("100% failure rate").
                Confidence = 0.0,
                SourceTraceIds = [],
                RiskLevel = ClassifyRisk(acc.Count),
                Status = RecipeProposalStatus.Pending,
                CreatedAt = now,
            });
        }

        // Sort: recency-boosted first (LastSeen within RecencyWeightHours → true support boost),
        // then by raw count descending.
        var cutoff = now - TimeSpan.FromHours(_options.RecencyWeightHours);
        proposals.Sort((a, b) =>
        {
            var aRecent = ParseLastSeen(a.Body) >= cutoff;
            var bRecent = ParseLastSeen(b.Body) >= cutoff;
            if (aRecent != bRecent) return aRecent ? -1 : 1;
            return b.Support.CompareTo(a.Support);
        });

        return proposals;
    }

    private RecipeProposalRiskLevel ClassifyRisk(int failureCount)
        => failureCount >= _options.MediumRiskMinFailureCount
            ? RecipeProposalRiskLevel.Medium
            : RecipeProposalRiskLevel.Low;

    private static (string AgentName, string? ToolName) ParseAttributionPath(string path)
    {
        // Shapes and their sources:
        //   1 segment  "agentId"               → agent-level signal (TurnFailed, LlmRetry…);
        //                                         AgentName = path, ToolName = null.
        //   2 segments "agentId/toolName"       → ToolError (from RunHealthSignalSubscriber);
        //              "mcpServerId/toolName"   → McpToolError synthesised by FailureSearchService
        //                                         (no agent info; first segment is server id).
        //   3 segments "agentId/mcpId/toolName" → ToolError with artifact annotation.
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length switch
        {
            0 => (string.Empty, null),
            1 => (parts[0], null),       // whole path = agent name, no tool component
            _ => (parts[0], parts[^1]),  // first = agent/server, last = tool
        };
    }

    private static DateTimeOffset ParseLastSeen(string body)
    {
        try
        {
            var prior = JsonSerializer.Deserialize<FailurePriorBody>(body);
            return prior?.LastSeen ?? DateTimeOffset.MinValue;
        }
        catch
        {
            return DateTimeOffset.MinValue;
        }
    }

    private sealed class GroupAccumulator
    {
        public int Count { get; private set; }
        public DateTimeOffset FirstSeen { get; private set; } = DateTimeOffset.MaxValue;
        public DateTimeOffset LastSeen { get; private set; } = DateTimeOffset.MinValue;

        public void Add(DateTimeOffset at)
        {
            Count++;
            if (at < FirstSeen) FirstSeen = at;
            if (at > LastSeen) LastSeen = at;
        }
    }
}
