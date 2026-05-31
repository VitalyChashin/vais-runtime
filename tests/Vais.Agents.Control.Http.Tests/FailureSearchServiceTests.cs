// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Vais.Agents.Observability.McpGatewayEventStore;
using Vais.Agents.Observability.RunHealthStore;
using Xunit;

namespace Vais.Agents.Control.Http.Tests;

/// <summary>
/// DM-10 — Part 2c FailureSearchService tests. Pin the cross-source merge and the
/// concept parent-walk symmetry across both backing stores.
/// </summary>
public sealed class FailureSearchServiceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 5, 31, 12, 0, 0, TimeSpan.Zero);

    // ── Parent-walk symmetry (advisor-caught correctness bug) ─────────────────

    [Fact]
    public async Task Search_ParentConcept_MatchesArtifactSubConcept_OnBusBranch()
    {
        // Subscriber stamped a sub-concept (artifact override) on a ToolError row.
        // Querying the parent concept ("ToolError") must return this row via parent-walk.
        var bus = new StubRunHealthStore
        {
            Rows =
            [
                new RunHealthSignalRecord(
                    RunId: "run-1",
                    CorrelationId: null,
                    Source: "search-tool",
                    Kind: RunHealthSignalKind.ToolError,
                    Level: FailureLevel.Warning,
                    ErrorType: "RateLimited",
                    IsTransient: true,
                    At: T0,
                    ConceptName: "ToolError/RateLimited",
                    AttributionPath: "agent/search-tool"),
            ],
        };
        var mcp = new StubMcpStore { Events = [] };
        var catalog = new TestCatalogWithChild("ToolError/RateLimited", "ToolError");

        var svc = new FailureSearchService(bus, mcp, catalog, null);
        var results = await svc.SearchAsync(new FailureSearchQuery(ConceptName: "ToolError"));

        results.Should().HaveCount(1,
            "querying the parent concept must match descendants via IsMatchOrDescendant — the bug the advisor caught was bus exact-match");
        results[0].ConceptName.Should().Be("ToolError/RateLimited");
    }

    [Fact]
    public async Task Search_ParentConcept_MatchesArtifactSubConcept_OnMcpBranch()
    {
        // MCP gateway event reports a failed tool; an artifact would refine concept to
        // McpToolError/AuthExpired. The query parent concept ("McpToolError") must match.
        var bus = new StubRunHealthStore { Rows = [] };
        var mcp = new StubMcpStore
        {
            Events =
            [
                new McpGatewayEvent(
                    EventId: "e1",
                    GatewayId: "gw1",
                    ToolName: "confluence_search",
                    EventKind: "call.failed",
                    DurationMs: 100,
                    CacheHit: false,
                    BlockedReason: null,
                    ErrorType: "Unauthorized",
                    At: T0,
                    CorrelationId: null,
                    RunId: "run-2"),
            ],
        };
        var catalog = AutoDerivedFailureOntologyCatalog.Instance; // McpToolError is base

        var svc = new FailureSearchService(bus, mcp, catalog, null);
        var results = await svc.SearchAsync(new FailureSearchQuery(ConceptName: "McpToolError"));

        results.Should().HaveCount(1);
        results[0].RunId.Should().Be("run-2");
        results[0].ConceptName.Should().Be("McpToolError");
        results[0].Source.Should().Be("confluence_search");
        results[0].AttributionPath.Should().Be("confluence_search",
            "no artifact bound → path is tool-name only");
    }

    // ── Source merge + sort ───────────────────────────────────────────────────

    [Fact]
    public async Task Search_NullConcept_MergesBothStores_SortedByAtDesc()
    {
        var bus = new StubRunHealthStore
        {
            Rows =
            [
                new RunHealthSignalRecord("run-A", null, "agent-x", RunHealthSignalKind.ToolError,
                    FailureLevel.Warning, "Boom", false, T0, "ToolError", "agent-x/tool"),
            ],
        };
        var mcp = new StubMcpStore
        {
            Events =
            [
                new McpGatewayEvent("e1", "gw", "tool-y", "call.failed", 50, false, null,
                    "Unauthorized", T0.AddMinutes(5), null, "run-B"),
            ],
        };
        var svc = new FailureSearchService(bus, mcp, AutoDerivedFailureOntologyCatalog.Instance, null);

        var results = await svc.SearchAsync(new FailureSearchQuery());

        results.Should().HaveCount(2);
        results[0].RunId.Should().Be("run-B", "MCP event @ T+5 must come before bus row @ T0");
        results[1].RunId.Should().Be("run-A");
    }

    [Fact]
    public async Task Search_McpStore_ArtifactRefinement_OverridesConceptAndExtendsPath()
    {
        var artifact = new FailureAttributionArtifact(
            Tools: new Dictionary<string, FailureToolAnnotation>
            {
                ["confluence_search"] = new(
                    Concept: "McpToolError/AuthExpired",
                    McpServerId: "confluence-mcp"),
            });
        var registry = new InMemoryFailureAttributionRegistry();
        registry.Register("confluence", artifact);

        var bus = new StubRunHealthStore { Rows = [] };
        var mcp = new StubMcpStore
        {
            Events =
            [
                new McpGatewayEvent("e1", "gw", "confluence_search", "call.failed", 50, false, null,
                    "Unauthorized", T0, null, "run-1"),
            ],
        };

        var svc = new FailureSearchService(bus, mcp, AutoDerivedFailureOntologyCatalog.Instance, registry);
        var results = await svc.SearchAsync(new FailureSearchQuery(ConceptName: "McpToolError"));

        results.Should().HaveCount(1, "the refined concept 'McpToolError/AuthExpired' descends from 'McpToolError'");
        results[0].ConceptName.Should().Be("McpToolError/AuthExpired");
        results[0].AttributionPath.Should().Be("confluence-mcp/confluence_search");
    }

    [Fact]
    public async Task Search_McpStore_AgentNameFilter_SkipsMcpEntirely()
    {
        // MCP events have no agent column — when an agentName filter is given, the
        // MCP branch is silently skipped (only the bus store can filter by agent).
        var bus = new StubRunHealthStore { Rows = [] };
        var mcp = new StubMcpStore
        {
            Events =
            [
                new McpGatewayEvent("e1", "gw", "tool", "call.failed", 50, false, null,
                    "Boom", T0, null, "run-1"),
            ],
        };

        var svc = new FailureSearchService(bus, mcp, AutoDerivedFailureOntologyCatalog.Instance, null);
        var results = await svc.SearchAsync(new FailureSearchQuery(AgentName: "some-agent"));
        results.Should().BeEmpty();
    }

    [Fact]
    public async Task Search_Limit_AppliedAfterMerge()
    {
        var bus = new StubRunHealthStore
        {
            Rows = Enumerable.Range(0, 3).Select(i =>
                new RunHealthSignalRecord($"run-{i}", null, $"a-{i}", RunHealthSignalKind.ToolError,
                    FailureLevel.Warning, "Err", false, T0.AddSeconds(i), "ToolError", null)).ToList(),
        };
        var mcp = new StubMcpStore { Events = [] };
        var svc = new FailureSearchService(bus, mcp, AutoDerivedFailureOntologyCatalog.Instance, null);

        var results = await svc.SearchAsync(new FailureSearchQuery(Limit: 2));
        results.Should().HaveCount(2);
    }

    // ── Stubs ─────────────────────────────────────────────────────────────────

    private sealed class StubRunHealthStore : IRunHealthStore
    {
        public IReadOnlyList<RunHealthSignalRecord> Rows { get; set; } = [];

        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task RecordSignalAsync(RunHealthSignalRecord record, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<RunHealthSignal>> ListByRunTreeAsync(string rootRunId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<RunHealthSignal>>([]);
        public Task DeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<RunHealthSignalRecord>> QuerySignalsAsync(
            string? conceptName = null, string? agentName = null, DateTimeOffset? since = null,
            int limit = 50, CancellationToken ct = default)
        {
            // Apply only filters the service is expected to pass to the store. Per the
            // bugfix, the service passes conceptName=null and post-filters via the catalog.
            IEnumerable<RunHealthSignalRecord> q = Rows;
            if (conceptName is not null) q = q.Where(r => r.ConceptName == conceptName);
            if (agentName is not null) q = q.Where(r => r.Source == agentName);
            if (since.HasValue) q = q.Where(r => r.At >= since.Value);
            return Task.FromResult<IReadOnlyList<RunHealthSignalRecord>>(q.Take(limit).ToList());
        }

        public Task<IReadOnlyList<RunHealthRunSummary>> ListDegradedRunsAsync(
            FailureLevel minLevel = FailureLevel.Warning, DateTimeOffset? since = null,
            int limit = 50, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<RunHealthRunSummary>>([]);
    }

    private sealed class StubMcpStore : IMcpGatewayEventStore
    {
        public IReadOnlyList<McpGatewayEvent> Events { get; set; } = [];

        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task RecordAsync(McpGatewayEvent evt, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<McpGatewayEvent>> ListAsync(string gatewayId, DateTimeOffset? since = null,
            DateTimeOffset? until = null, string? toolName = null, string? kind = null, int limit = 50,
            CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<McpGatewayEvent>>([]);
        public Task<IReadOnlyList<McpGatewayEvent>> ListByRunAsync(string runId, int limit = 200, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<McpGatewayEvent>>([]);
        public Task DeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<McpGatewayEvent>> QueryFailedAcrossGatewaysAsync(
            string? toolName = null, DateTimeOffset? since = null, int limit = 50, CancellationToken ct = default)
        {
            var rows = Events.AsEnumerable();
            if (toolName is not null) rows = rows.Where(e => e.ToolName == toolName);
            if (since.HasValue) rows = rows.Where(e => e.At >= since.Value);
            return Task.FromResult<IReadOnlyList<McpGatewayEvent>>(rows.Take(limit).ToList());
        }
    }

    /// <summary>
    /// Catalog with one extra child concept inserted under a parent — proves
    /// IsMatchOrDescendant returns true for the artifact-supplied descendant.
    /// </summary>
    private sealed class TestCatalogWithChild : IFailureOntologyCatalog
    {
        private readonly IFailureOntologyCatalog _base = AutoDerivedFailureOntologyCatalog.Instance;
        private readonly FailureConcept _extra;

        public TestCatalogWithChild(string childName, string parentName)
        {
            _extra = new FailureConcept(
                Name: childName,
                Axis: FailureAxis.Mechanical,
                DefaultLevel: FailureLevel.Warning,
                Description: "test child",
                SourceKinds: [],
                ParentName: parentName);
        }

        public string OntologyVersion => _base.OntologyVersion;
        public IReadOnlyCollection<FailureConcept> Concepts => [.. _base.Concepts, _extra];
        public FailureConcept? Get(string conceptName) => conceptName == _extra.Name ? _extra : _base.Get(conceptName);
        public FailureConcept? FromSignalKind(RunHealthSignalKind kind) => _base.FromSignalKind(kind);

        public bool IsMatchOrDescendant(string candidateName, string filterName)
        {
            if (string.Equals(candidateName, filterName, StringComparison.Ordinal)) return true;
            var c = Get(candidateName);
            while (c?.ParentName is not null)
            {
                if (string.Equals(c.ParentName, filterName, StringComparison.Ordinal)) return true;
                c = Get(c.ParentName);
            }
            return false;
        }

        public IReadOnlyList<(string AttributionPath, FailurePriorBody Prior)> GetPriorsForConcept(
            string conceptName) => [];
    }
}
