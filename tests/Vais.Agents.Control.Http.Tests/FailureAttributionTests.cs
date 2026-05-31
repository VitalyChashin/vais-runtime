// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using FluentAssertions;
using Vais.Agents.Control.Manifests;
using Vais.Agents.Observability.RunHealthStore;
using Xunit;

namespace Vais.Agents.Control.Http.Tests;

/// <summary>
/// Part 2b — failure attribution binding (FA-2, FA-8, exit criteria #1–#3).
/// Tests call production code directly: loader, registry, subscriber's StampAttribution,
/// aggregator's BuildMcpAttributionPath + TryExtractAgentFromRunId, enricher InvokeAsync.
/// </summary>
public sealed class FailureAttributionTests
{
    private const string ArtifactJson = """
        {
          "ontologyVersion": "test-1.0",
          "tools": {
            "confluence_search": {
              "concept": "McpToolError/AuthExpired",
              "mcpServerId": "confluence-mcp",
              "tags": ["auth"]
            }
          },
          "agents": {
            "confluence-agent": {
              "concept": "McpToolError"
            }
          }
        }
        """;

    private static readonly DateTimeOffset At = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static FailureAttributionArtifact LoadArtifact() =>
        FailureAttributionArtifactLoader.LoadFromJson(ArtifactJson);

    // ── FA-2: Loader ─────────────────────────────────────────────────────────────

    [Fact]
    public void Loader_DeserializesArtifact()
    {
        var a = LoadArtifact();

        a.OntologyVersion.Should().Be("test-1.0");
        a.Tools!["confluence_search"].Concept.Should().Be("McpToolError/AuthExpired");
        a.Tools!["confluence_search"].McpServerId.Should().Be("confluence-mcp");
        a.Tools!["confluence_search"].Tags.Should().Contain("auth");
        a.Agents!["confluence-agent"].Concept.Should().Be("McpToolError");
    }

    [Fact]
    public void Loader_EmptyJson_ReturnsEmptyArtifact()
    {
        var a = FailureAttributionArtifactLoader.LoadFromJson("{}");
        a.Tools.Should().BeNullOrEmpty();
        a.Agents.Should().BeNullOrEmpty();
    }

    // ── Registry ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Registry_RegisterAndGet()
    {
        var reg = new InMemoryFailureAttributionRegistry();
        reg.Register("my-ref", LoadArtifact());

        reg.Get("my-ref").Should().NotBeNull();
        reg.Get("missing").Should().BeNull();
        reg.Names.Should().Contain("my-ref");
    }

    // ── Index ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Index_RegisterAndTryGet()
    {
        var idx = new InMemoryFailureAttributionIndex();
        idx.Register("confluence-agent", "my-ref");

        idx.TryGet("confluence-agent", out var found).Should().BeTrue();
        found.Should().Be("my-ref");
        idx.TryGet("unknown", out _).Should().BeFalse();
    }

    // ── FA-8: RunHealthSignalSubscriber.StampAttribution ─────────────────────────

    [Fact]
    public void StampAttribution_BasicPath_WhenNoArtifact()
    {
        var ctx = AgentContext.Empty with { RunId = "r1", AgentName = "my-agent" };
        var evt = new ToolCallCompleted(At, ctx, "c1", "search", Succeeded: false, Error: "err", TimeSpan.Zero);
        var record = RunHealthSignalSubscriber.Map(evt)!;

        var stamped = RunHealthSignalSubscriber.StampAttribution(record, "my-agent", null, null);

        stamped.AttributionPath.Should().Be("my-agent/search");
        stamped.ConceptName.Should().BeNull(); // no catalog here; only path changes
    }

    [Fact]
    public void StampAttribution_EnhancedPath_WithArtifact()
    {
        var ctx = AgentContext.Empty with { RunId = "r1", AgentName = "confluence-agent" };
        var evt = new ToolCallCompleted(At, ctx, "c1", "confluence_search",
            Succeeded: false, Error: "err", TimeSpan.Zero);
        var record = RunHealthSignalSubscriber.Map(evt)!;

        var reg = new InMemoryFailureAttributionRegistry();
        reg.Register("test-ref", LoadArtifact());
        var idx = new InMemoryFailureAttributionIndex();
        idx.Register("confluence-agent", "test-ref");

        var stamped = RunHealthSignalSubscriber.StampAttribution(record, "confluence-agent", reg, idx);

        stamped.AttributionPath.Should().Be("confluence-agent/confluence-mcp/confluence_search",
            "artifact provides mcpServerId=confluence-mcp for this tool");
        stamped.ConceptName.Should().Be("McpToolError/AuthExpired",
            "artifact provides concept override for confluence_search");
    }

    [Fact]
    public void StampAttribution_AgentLevel_WhenTurnFailed()
    {
        var ctx = AgentContext.Empty with { RunId = "r1", AgentName = "confluence-agent" };
        var evt = new TurnFailed(At, ctx, "System.Exception", "boom", TimeSpan.Zero);
        var record = RunHealthSignalSubscriber.Map(evt)!;

        var reg = new InMemoryFailureAttributionRegistry();
        reg.Register("test-ref", LoadArtifact());
        var idx = new InMemoryFailureAttributionIndex();
        idx.Register("confluence-agent", "test-ref");

        var stamped = RunHealthSignalSubscriber.StampAttribution(record, "confluence-agent", reg, idx);

        // TurnFailed source = agent name (same as agentId) → path is just agentId
        stamped.AttributionPath.Should().Be("confluence-agent");
        stamped.ConceptName.Should().Be("McpToolError",
            "artifact provides agent-level concept for confluence-agent");
    }

    [Fact]
    public void StampAttribution_NoAgentName_ReturnsUnchanged()
    {
        var ctx = AgentContext.Empty with { RunId = "r1" }; // RunId required by Map; AgentName empty
        var evt = new ToolCallCompleted(At, ctx, "c1", "tool",
            Succeeded: false, Error: "err", TimeSpan.Zero);
        var record = RunHealthSignalSubscriber.Map(evt)!;

        var stamped = RunHealthSignalSubscriber.StampAttribution(record, null, null, null);
        stamped.AttributionPath.Should().BeNull("no agent name → no attribution possible");
    }

    // ── Aggregator helpers ───────────────────────────────────────────────────────

    [Fact]
    public void TryExtractAgentFromRunId_SubRun_ExtractsAgentName()
    {
        RunHealthAggregator.TryExtractAgentFromRunId("root__confluence-agent__abc123")
            .Should().Be("confluence-agent");
    }

    [Fact]
    public void TryExtractAgentFromRunId_RootRun_ReturnsNull()
    {
        RunHealthAggregator.TryExtractAgentFromRunId("root-run-id").Should().BeNull();
        RunHealthAggregator.TryExtractAgentFromRunId(null).Should().BeNull();
        RunHealthAggregator.TryExtractAgentFromRunId("a__b").Should().BeNull("needs 3+ parts");
    }

    [Fact]
    public void BuildMcpAttributionPath_WithArtifact_RefinesTool()
    {
        var reg = new InMemoryFailureAttributionRegistry();
        reg.Register("test-ref", LoadArtifact());

        string? conceptName = "McpToolError"; // base
        var path = RunHealthAggregator.BuildMcpAttributionPath("confluence_search", reg, ref conceptName);

        path.Should().Be("confluence-mcp/confluence_search",
            "artifact provides mcpServerId for this tool");
        conceptName.Should().Be("McpToolError/AuthExpired",
            "artifact concept overrides the base catalog concept");
    }

    [Fact]
    public void BuildMcpAttributionPath_NoArtifact_ReturnsToolName()
    {
        string? conceptName = "McpToolError";
        var path = RunHealthAggregator.BuildMcpAttributionPath("some_tool", null, ref conceptName);

        path.Should().Be("some_tool");
        conceptName.Should().Be("McpToolError", "no override when no artifact");
    }

    // ── FailureAttributionEnricher ───────────────────────────────────────────────

    [Fact]
    public async Task Enricher_FailedCall_EmitsTeeEvent()
    {
        var captured = new List<InterceptorTeeEvent>();
        var tee = new CapturingTee(captured);
        var enricher = new FailureAttributionEnricher(
            tee,
            LoadArtifact(),
            AutoDerivedFailureOntologyCatalog.Instance);

        var ctx = new ToolGatewayContext("confluence_search", "c1",
            JsonDocument.Parse("{}").RootElement,
            AgentContext.Empty with { AgentName = "confluence-agent" });

        var result = await enricher.InvokeAsync(ctx,
            () => Task.FromResult(new ToolCallOutcome("c1", Result: null, Error: "unauthorized")),
            default);

        result.Error.Should().Be("unauthorized");
        captured.Should().HaveCount(1, "one failure event emitted");
        var payload = captured[0].Payload as FailureAttributionPayload;
        payload.Should().NotBeNull();
        payload!.ToolName.Should().Be("confluence_search");
        payload.ConceptName.Should().Be("McpToolError/AuthExpired");
        payload.AttributionPath.Should().Be("confluence-agent/confluence-mcp/confluence_search");
    }

    [Fact]
    public async Task Enricher_SuccessfulCall_NoTeeEvent()
    {
        var captured = new List<InterceptorTeeEvent>();
        var tee = new CapturingTee(captured);
        var enricher = new FailureAttributionEnricher(tee, null, null);

        var ctx = new ToolGatewayContext("search", "c1",
            JsonDocument.Parse("{}").RootElement, AgentContext.Empty);

        await enricher.InvokeAsync(ctx,
            () => Task.FromResult(new ToolCallOutcome("c1", Result: "ok")), default);

        captured.Should().BeEmpty("no tee event on success");
    }

    [Fact]
    public async Task Enricher_NoTee_DoesNotThrow()
    {
        var enricher = new FailureAttributionEnricher(null, null, null);
        var ctx = new ToolGatewayContext("tool", "c1",
            JsonDocument.Parse("{}").RootElement, AgentContext.Empty);

        var act = () => enricher.InvokeAsync(ctx,
            () => Task.FromResult(new ToolCallOutcome("c1", Result: null, Error: "err")), default);
        await act.Should().NotThrowAsync();
    }

    // ── Fakes ────────────────────────────────────────────────────────────────────

    private sealed class CapturingTee(List<InterceptorTeeEvent> captured) : IInterceptorTee
    {
        public ValueTask EmitAsync(InterceptorTeeEvent evt, CancellationToken ct = default)
        {
            captured.Add(evt);
            return ValueTask.CompletedTask;
        }
    }

}
