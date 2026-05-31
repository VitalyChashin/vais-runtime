// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Vais.Agents.Control.Manifests;
using Xunit;

namespace Vais.Agents.Control.Http.Tests;

/// <summary>
/// Part 2a — overlay loader round-trip (FT-3, exit criterion #1).
/// Verifies that JSON-authored overlays (string enums, empty sourceKinds, tags)
/// deserialize correctly and that the OverlaidFailureOntologyCatalog merges them
/// over the auto-derived base.
/// </summary>
public sealed class FailureOntologyOverlayLoaderTests
{
    private const string SubConceptJson = """
        {
          "concepts": [
            {
              "name": "McpToolError/AuthExpired",
              "axis": "Mechanical",
              "defaultLevel": "Warning",
              "description": "MCP tool call failed due to expired auth token.",
              "sourceKinds": [],
              "parentName": "McpToolError",
              "tags": ["auth", "transient"]
            }
          ]
        }
        """;

    // ── LoadFromJson ─────────────────────────────────────────────────────────────

    [Fact]
    public void LoadFromJson_DeserializesStringEnums()
    {
        var overlay = FailureOntologyOverlayLoader.LoadFromJson(SubConceptJson);

        overlay.Should().NotBeNull();
        overlay.Concepts.Should().HaveCount(1);
        var c = overlay.Concepts![0];
        c.Name.Should().Be("McpToolError/AuthExpired");
        c.Axis.Should().Be(FailureAxis.Mechanical);
        c.DefaultLevel.Should().Be(FailureLevel.Warning);
        c.ParentName.Should().Be("McpToolError");
    }

    [Fact]
    public void LoadFromJson_Tags_RoundTrip()
    {
        var overlay = FailureOntologyOverlayLoader.LoadFromJson(SubConceptJson);
        overlay.Concepts![0].Tags.Should().Contain("auth").And.Contain("transient");
    }

    [Fact]
    public void LoadFromJson_EmptySourceKinds_DeserializesAsEmptyList()
    {
        var overlay = FailureOntologyOverlayLoader.LoadFromJson(SubConceptJson);
        var sourceKinds = overlay.Concepts![0].SourceKinds;
        sourceKinds.Should().NotBeNull("SourceKinds must not be null even when JSON has []");
        sourceKinds.Should().BeEmpty();
    }

    [Fact]
    public void LoadFromJson_EmptyJson_ReturnsEmptyOverlay()
    {
        var overlay = FailureOntologyOverlayLoader.LoadFromJson("{}");
        overlay.Concepts.Should().BeNullOrEmpty();
        overlay.SeverityRules.Should().BeNullOrEmpty();
    }

    // ── OverlaidFailureOntologyCatalog ───────────────────────────────────────────

    [Fact]
    public void OverlaidCatalog_Get_FindsSubConcept()
    {
        var overlay = FailureOntologyOverlayLoader.LoadFromJson(SubConceptJson);
        var catalog = new OverlaidFailureOntologyCatalog(overlay);

        var sub = catalog.Get("McpToolError/AuthExpired");
        sub.Should().NotBeNull();
        sub!.ParentName.Should().Be("McpToolError");
        sub.Tags.Should().Contain("auth");
    }

    [Fact]
    public void OverlaidCatalog_BaseConcepts_StillPresent()
    {
        var overlay = FailureOntologyOverlayLoader.LoadFromJson(SubConceptJson);
        var catalog = new OverlaidFailureOntologyCatalog(overlay);

        catalog.Get("McpToolError").Should().NotBeNull("base concept must survive overlay merge");
        catalog.Get("ToolError").Should().NotBeNull();
    }

    [Fact]
    public void OverlaidCatalog_IsMatchOrDescendant_SubConceptMatchesParent()
    {
        var overlay = FailureOntologyOverlayLoader.LoadFromJson(SubConceptJson);
        var catalog = new OverlaidFailureOntologyCatalog(overlay);

        catalog.IsMatchOrDescendant("McpToolError/AuthExpired", "McpToolError")
            .Should().BeTrue("sub-concept is a descendant of its parent");
        catalog.IsMatchOrDescendant("McpToolError", "McpToolError/AuthExpired")
            .Should().BeFalse("parent is NOT a descendant of its child");
    }

    [Fact]
    public void OverlaidCatalog_SubConceptSourceKinds_NotNull()
    {
        var overlay = FailureOntologyOverlayLoader.LoadFromJson(SubConceptJson);
        var catalog = new OverlaidFailureOntologyCatalog(overlay);

        var sub = catalog.Get("McpToolError/AuthExpired")!;
        sub.SourceKinds.Should().NotBeNull("SourceKinds normalized to [] by OverlaidCatalog ctor");
    }
}
