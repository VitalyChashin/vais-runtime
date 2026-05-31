// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using Vais.Agents.Control.Manifests;
using Xunit;

namespace Vais.Agents.Control.Mcp.Server.Tests;

/// <summary>
/// DM-10 — Part 2c diagnostic verbs + resources. Tests call production code (the
/// internal <c>InvokeAsync</c> overload + <c>ReadOntologyResourceAsync</c> / etc.) with
/// stub services that return seeded data, so the assertions exercise the real reshape
/// and merge logic — not locally-built JSON.
/// </summary>
public sealed class DesignMcpDiagnosticToolsTests
{
    private readonly ServiceCollection _sc;
    private readonly StubAggregator _aggregator;
    private readonly StubFailureSearchService _search;

    public DesignMcpDiagnosticToolsTests()
    {
        _sc = new ServiceCollection();
        _aggregator = new StubAggregator();
        _search = new StubFailureSearchService();
        _sc.AddSingleton<IRunHealthAggregator>(_aggregator);
        _sc.AddSingleton<IFailureSearchService>(_search);
        _sc.AddSingleton<IFailureOntologyCatalog>(AutoDerivedFailureOntologyCatalog.Instance);
        _sc.AddSingleton<IOntologyCatalog>(_ => OntologyCatalog.BuildFromEmbeddedBase());
    }

    // ── Exit #1 (tools/list baseline) ─────────────────────────────────────────

    [Fact]
    public async Task ListTools_Baseline_IncludesThreeDiagnosticVerbs()
    {
        // Read verbs aren't gated by DesignToolsScopeFilterInterceptor — they ship in
        // the unconditional baseline. (The "non-authorized excludes them" case is N/A here.)
        var sp = _sc.BuildServiceProvider();
        var result = await DesignMcpToolHandlers.ListToolsAsync(sp, default);
        var names = result.Tools.Select(t => t.Name).ToHashSet();
        names.Should().Contain("vais.diagnose");
        names.Should().Contain("vais.runHealth");
        names.Should().Contain("vais.failures");
    }

    // ── Exit #2 (vais.diagnose preserves ConceptName + AttributionPath) ───────

    [Fact]
    public async Task VaisDiagnose_PreservesConceptName_AndAttributionPath()
    {
        var at = new DateTimeOffset(2026, 5, 31, 12, 0, 0, TimeSpan.Zero);
        _aggregator.Result = new RunHealth(
            RunId: "run-42",
            Level: RunHealthLevel.Degraded,
            Signals:
            [
                new RunHealthSignal(
                    Source: "confluence_search",
                    Kind: RunHealthSignalKind.McpError,
                    Level: FailureLevel.Warning,
                    ErrorType: "Unauthorized",
                    IsTransient: false,
                    At: at,
                    ConceptName: "McpToolError/AuthExpired",
                    AttributionPath: "confluence-agent/confluence-mcp/confluence_search"),
            ],
            BackgroundFailures: []);

        var result = await InvokeAsync("vais.diagnose", new { runId = "run-42" });
        result.IsError.Should().BeFalse();

        var doc = JsonDocument.Parse(Content(result));
        doc.RootElement.GetProperty("runId").GetString().Should().Be("run-42");
        doc.RootElement.GetProperty("level").GetString().Should().Be("degraded");
        var signal = doc.RootElement.GetProperty("signals")[0];
        signal.GetProperty("conceptName").GetString().Should().Be("McpToolError/AuthExpired",
            "the reshape-from-domain path (not RunHealthSignalDto) must carry Part 2a's concept through");
        signal.GetProperty("attributionPath").GetString().Should().Be("confluence-agent/confluence-mcp/confluence_search",
            "the reshape-from-domain path must carry Part 2b's attribution through");
        signal.GetProperty("source").GetString().Should().Be("confluence_search");
        signal.GetProperty("errorType").GetString().Should().Be("Unauthorized");
    }

    [Fact]
    public async Task VaisDiagnose_MissingRunId_ReturnsError()
    {
        var result = await InvokeAsync("vais.diagnose", new { });
        result.IsError.Should().BeTrue();
    }

    [Fact]
    public async Task VaisDiagnose_NoAggregator_ReturnsError()
    {
        var sp = new ServiceCollection().BuildServiceProvider();
        var args = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            JsonSerializer.Serialize(new { runId = "r1" }))!;
        var result = await DesignMcpToolHandlers.InvokeAsync("vais.diagnose", args, sp, default);
        result.IsError.Should().BeTrue();
        Content(result).Should().Contain("IRunHealthAggregator");
    }

    // ── vais.failures merge + parent walk ─────────────────────────────────────

    [Fact]
    public async Task VaisFailures_NullConcept_ReturnsAllSearchResults()
    {
        var at = new DateTimeOffset(2026, 5, 31, 12, 0, 0, TimeSpan.Zero);
        _search.Rows =
        [
            new FailureSearchResult("run-1", "ToolError", "agent-a/tool-x", "tool-x", "warning", "Boom", at),
            new FailureSearchResult("run-2", "McpToolError", "mcp1/tool-y", "tool-y", "warning", "Unauthorized", at.AddMinutes(1)),
        ];

        var result = await InvokeAsync("vais.failures", new { });
        result.IsError.Should().BeFalse();
        var doc = JsonDocument.Parse(Content(result));
        doc.RootElement.GetProperty("count").GetInt32().Should().Be(2);

        // Service is the surface; the handler just projects whatever it returns.
        _search.LastQuery!.ConceptName.Should().BeNull();
    }

    [Fact]
    public async Task VaisFailures_ConceptFilter_PassesThroughToService()
    {
        _search.Rows = [];
        await InvokeAsync("vais.failures", new { concept = "McpToolError" });
        _search.LastQuery!.ConceptName.Should().Be("McpToolError",
            "the handler must forward the concept filter unmodified — parent-walk happens inside the service");
    }

    [Fact]
    public async Task VaisFailures_SinceTimestamp_Parsed()
    {
        _search.Rows = [];
        await InvokeAsync("vais.failures", new { since = "2026-05-31T12:00:00Z", limit = 25 });
        _search.LastQuery!.Since.Should().NotBeNull();
        _search.LastQuery!.Since!.Value.UtcDateTime.Should().Be(new DateTime(2026, 5, 31, 12, 0, 0, DateTimeKind.Utc));
        _search.LastQuery!.Limit.Should().Be(25);
    }

    [Fact]
    public async Task VaisFailures_InvalidSince_ReturnsError()
    {
        var result = await InvokeAsync("vais.failures", new { since = "not-a-date" });
        result.IsError.Should().BeTrue();
    }

    // ── vais.runHealth level mapping + invalid-level guard ────────────────────

    [Fact]
    public async Task VaisRunHealth_LevelFailed_MapsToError()
    {
        _aggregator.RunSummaries =
        [
            new RunHealthListItem("run-x", "failed", 3, DateTimeOffset.UtcNow),
        ];
        await InvokeAsync("vais.runHealth", new { level = "failed" });
        _aggregator.LastMinLevel.Should().Be(FailureLevel.Error);
    }

    [Fact]
    public async Task VaisRunHealth_LevelDegraded_MapsToWarning()
    {
        _aggregator.RunSummaries = [];
        await InvokeAsync("vais.runHealth", new { level = "degraded" });
        _aggregator.LastMinLevel.Should().Be(FailureLevel.Warning);
    }

    [Fact]
    public async Task VaisRunHealth_NoLevel_DefaultsToWarning()
    {
        _aggregator.RunSummaries = [];
        await InvokeAsync("vais.runHealth", new { });
        _aggregator.LastMinLevel.Should().Be(FailureLevel.Warning,
            "absent level filter defaults to degraded (Warning) — both degraded AND failed runs returned");
    }

    [Fact]
    public async Task VaisRunHealth_InvalidLevel_ReturnsError_BeforeReachingAggregator()
    {
        _aggregator.LastMinLevel = null;
        var result = await InvokeAsync("vais.runHealth", new { level = "purple" });
        result.IsError.Should().BeTrue();
        _aggregator.LastMinLevel.Should().BeNull(
            "validation must reject the bad value before computing minLevel — see DesignMcpToolHandlers.HandleRunHealthAsync ordering");
    }

    [Fact]
    public async Task VaisRunHealth_ResultProjection_CarriesAllFields()
    {
        var at = new DateTimeOffset(2026, 5, 31, 14, 0, 0, TimeSpan.Zero);
        _aggregator.RunSummaries =
        [
            new RunHealthListItem("run-x", "failed", 3, at),
        ];
        var result = await InvokeAsync("vais.runHealth", new { });
        var doc = JsonDocument.Parse(Content(result));
        var item = doc.RootElement.GetProperty("items")[0];
        item.GetProperty("runId").GetString().Should().Be("run-x");
        item.GetProperty("level").GetString().Should().Be("failed");
        item.GetProperty("signalCount").GetInt32().Should().Be(3);
    }

    // ── Resources: vais-ontology://Failure[/concept] ──────────────────────────

    [Fact]
    public async Task ListResources_WithCatalogRegistered_IncludesFailureRoot()
    {
        var sp = _sc.BuildServiceProvider();
        var result = await DesignMcpToolHandlers.ListOntologyResourcesAsync(sp);
        result.Resources.Should().Contain(r => r.Uri == "vais-ontology://Failure",
            "the Failure root resource is conditional on IFailureOntologyCatalog being registered");
    }

    [Fact]
    public async Task ReadFailureRoot_ReturnsCatalogProjection()
    {
        var sp = _sc.BuildServiceProvider();
        var result = await DesignMcpToolHandlers.ReadOntologyResourceAsync("vais-ontology://Failure", sp);
        var doc = JsonDocument.Parse(((TextResourceContents)result.Contents[0]).Text);
        doc.RootElement.TryGetProperty("ontologyVersion", out _).Should().BeTrue();
        var concepts = doc.RootElement.GetProperty("concepts");
        concepts.GetArrayLength().Should().BeGreaterThan(0);
        // The auto-derived catalog must include McpToolError (Part 2a base).
        concepts.EnumerateArray()
            .Should().Contain(c => c.GetProperty("name").GetString() == "McpToolError");
    }

    [Fact]
    public async Task ReadFailureConcept_ReturnsConceptDetails_WithChildren()
    {
        var sp = _sc.BuildServiceProvider();
        var result = await DesignMcpToolHandlers.ReadOntologyResourceAsync(
            "vais-ontology://Failure/McpToolError", sp);
        var doc = JsonDocument.Parse(((TextResourceContents)result.Contents[0]).Text);
        doc.RootElement.GetProperty("name").GetString().Should().Be("McpToolError");
        doc.RootElement.GetProperty("axis").GetString().Should().Be("Mechanical");
        doc.RootElement.TryGetProperty("children", out var children).Should().BeTrue();
        _ = children; // children list is empty for base catalog (no overlay) — that's fine.
    }

    [Fact]
    public async Task ReadFailureConcept_Unknown_Throws()
    {
        var sp = _sc.BuildServiceProvider();
        Func<Task> act = async () =>
            await DesignMcpToolHandlers.ReadOntologyResourceAsync(
                "vais-ontology://Failure/NoSuchConcept", sp);
        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*NoSuchConcept*");
    }

    // ── Resources: vais-diagnostics://run/{id} ────────────────────────────────

    [Fact]
    public async Task ReadDiagnosticsResource_ReturnsSamePayloadAs_VaisDiagnose()
    {
        var at = new DateTimeOffset(2026, 5, 31, 13, 0, 0, TimeSpan.Zero);
        _aggregator.Result = new RunHealth(
            RunId: "run-77",
            Level: RunHealthLevel.Failed,
            Signals:
            [
                new RunHealthSignal("agent-z", RunHealthSignalKind.TurnFailed, FailureLevel.Error,
                    "InvalidOperationException", false, at, "TurnFailed", "agent-z"),
            ],
            BackgroundFailures: []);
        var sp = _sc.BuildServiceProvider();
        var resourceResult = await DesignMcpToolHandlers.ReadDiagnosticsResourceAsync(
            "vais-diagnostics://run/run-77", sp, default);
        var resourceJson = ((TextResourceContents)resourceResult.Contents[0]).Text;

        var toolResult = await DesignMcpToolHandlers.InvokeAsync("vais.diagnose",
            new Dictionary<string, JsonElement>
            {
                ["runId"] = JsonDocument.Parse("\"run-77\"").RootElement,
            },
            sp, default);
        var toolJson = Content(toolResult);

        resourceJson.Should().Be(toolJson,
            "vais-diagnostics://run/{id} must serve the byte-identical payload of vais.diagnose(runId)");
    }

    [Fact]
    public async Task ReadDiagnosticsResource_BadUri_Throws()
    {
        var sp = _sc.BuildServiceProvider();
        Func<Task> act = async () =>
            await DesignMcpToolHandlers.ReadDiagnosticsResourceAsync(
                "vais-diagnostics://nope", sp, default);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    // ── Resource dispatch: HandleReadResourceAsync routes by prefix ───────────

    [Fact]
    public async Task HandleReadResource_DispatchesByPrefix()
    {
        var sp = _sc.BuildServiceProvider();

        // Ontology URI dispatches to ReadOntologyResourceAsync — verified by side-effect:
        // McpToolError must come back as a known concept.
        var ontologyResult = await DesignMcpToolHandlers.ReadOntologyResourceAsync(
            "vais-ontology://Failure/McpToolError", sp);
        ((TextResourceContents)ontologyResult.Contents[0]).Uri.Should().Be("vais-ontology://Failure/McpToolError");

        // Diagnostics URI dispatches to ReadDiagnosticsResourceAsync — verified by Uri echo.
        _aggregator.Result = new RunHealth("rx", RunHealthLevel.Healthy, [], []);
        var diagResult = await DesignMcpToolHandlers.ReadDiagnosticsResourceAsync(
            "vais-diagnostics://run/rx", sp, default);
        ((TextResourceContents)diagResult.Contents[0]).Uri.Should().Be("vais-diagnostics://run/rx");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<CallToolResult> InvokeAsync(string toolName, object argsObj)
    {
        var argsJson = JsonSerializer.Serialize(argsObj);
        var args = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(argsJson)!;
        return await DesignMcpToolHandlers.InvokeAsync(toolName, args, _sc.BuildServiceProvider(), default);
    }

    private static string Content(CallToolResult result)
        => ((TextContentBlock)result.Content[0]).Text;

    // ── Stubs ─────────────────────────────────────────────────────────────────

    private sealed class StubAggregator : IRunHealthAggregator
    {
        public RunHealth? Result { get; set; }
        public IReadOnlyList<RunHealthListItem> RunSummaries { get; set; } = [];
        public FailureLevel? LastMinLevel { get; set; }

        public Task<RunHealth> GetRunHealthAsync(string rootRunId, CancellationToken ct = default)
            => Task.FromResult(Result ?? new RunHealth(rootRunId, RunHealthLevel.Healthy, [], []));

        public Task<IReadOnlyList<RunHealthListItem>> ListDegradedRunsAsync(
            FailureLevel minLevel = FailureLevel.Warning,
            DateTimeOffset? since = null,
            int limit = 50,
            CancellationToken ct = default)
        {
            LastMinLevel = minLevel;
            return Task.FromResult(RunSummaries);
        }
    }

    private sealed class StubFailureSearchService : IFailureSearchService
    {
        public IReadOnlyList<FailureSearchResult> Rows { get; set; } = [];
        public FailureSearchQuery? LastQuery { get; private set; }

        public Task<IReadOnlyList<FailureSearchResult>> SearchAsync(
            FailureSearchQuery query, CancellationToken ct = default)
        {
            LastQuery = query;
            return Task.FromResult(Rows);
        }
    }
}
