// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Vais.Agents.Control.Manifests;
using Xunit;

namespace Vais.Agents.Runtime.Instantiation.Tests;

/// <summary>
/// Plan C2-3: when an <see cref="IAgentCapabilityMapBuilder"/> is registered, the
/// translator routes the LocalAgentTool's effective description through the map's
/// per-sub-agent <see cref="SubAgentCapability.Description"/> — so a deployer-overlaid
/// builder can replace sub-agent descriptions the LLM sees. Default builder = no
/// observable change (byte-identical to the prior <c>localRef ?? target</c> path).
/// </summary>
public sealed class SubAgentDescriptionOverlayTests
{
    [Fact]
    public async Task LocalAgentTool_DescriptionComesFromCapabilityMap_WhenBuilderOverlays()
    {
        var fixture = new TranslatorFixture()
            .WithProvider("openai")
            .WithCapabilityMapBuilder(new OverlayingBuilder())
            .WithManifest(BuildSubAgent("code-reviewer", "Original reviewer description."))
            .WithManifest(BuildCoordinator("coord", subToolName: "reviewer", subAgentId: "code-reviewer"));

        var options = await fixture.Translator.TranslateAsync("coord");

        var reviewerTool = options.ToolRegistry?.Tools.FirstOrDefault(t => t.Name == "reviewer");
        reviewerTool.Should().NotBeNull();
        reviewerTool!.Description.Should().StartWith("OVERLAID:",
            "the registered IAgentCapabilityMapBuilder overlayed the description for the LLM");
    }

    [Fact]
    public async Task LocalAgentTool_DescriptionFallsBackToManifestPath_WhenNoBuilderRegistered()
    {
        // No WithCapabilityMapBuilder → translator skips the overlay step entirely.
        var fixture = new TranslatorFixture()
            .WithProvider("openai")
            .WithManifest(BuildSubAgent("code-reviewer", "Original reviewer description."))
            .WithManifest(BuildCoordinator("coord", subToolName: "reviewer", subAgentId: "code-reviewer"));

        var options = await fixture.Translator.TranslateAsync("coord");

        var reviewerTool = options.ToolRegistry?.Tools.FirstOrDefault(t => t.Name == "reviewer");
        reviewerTool!.Description.Should().Be("Original reviewer description.",
            "without a builder the legacy localRef.Description ?? target.Description path applies unchanged");
    }

    [Fact]
    public async Task LocalAgentTool_NullOrEmptyOverlay_FallsBackToManifestDescription()
    {
        var fixture = new TranslatorFixture()
            .WithProvider("openai")
            .WithCapabilityMapBuilder(new ReturnsEmptyDescriptionBuilder())
            .WithManifest(BuildSubAgent("code-reviewer", "Original reviewer description."))
            .WithManifest(BuildCoordinator("coord", subToolName: "reviewer", subAgentId: "code-reviewer"));

        var options = await fixture.Translator.TranslateAsync("coord");

        var reviewerTool = options.ToolRegistry?.Tools.FirstOrDefault(t => t.Name == "reviewer");
        reviewerTool!.Description.Should().Be("Original reviewer description.",
            "empty / null overlay value must not silently blank the description");
    }

    // ── manifest helpers ──────────────────────────────────────────────────────

    private static AgentManifest BuildSubAgent(string id, string description) =>
        new(id, "1.0",
            new AgentHandlerRef("default", "maf"),
            [new ProtocolBinding("openai")],
            Tools: [])
        {
            Model = new ModelSpec("openai", "gpt-4o-mini"),
            Description = description,
        };

    private static AgentManifest BuildCoordinator(string coordinatorId, string subToolName, string subAgentId) =>
        new(coordinatorId, "1.0",
            new AgentHandlerRef("default", "maf"),
            [new ProtocolBinding("openai")],
            Tools: [new ToolRef(subToolName, Source: $"agent:{subToolName}Local")])
        {
            Model = new ModelSpec("openai", "gpt-4o-mini"),
            LocalAgents = [new LocalAgentRef($"{subToolName}Local", AgentId: subAgentId)],
        };

    // ── fake builders ─────────────────────────────────────────────────────────

    private sealed class OverlayingBuilder : IAgentCapabilityMapBuilder
    {
        public ValueTask<CapabilityMap> BuildAsync(string coordinatorAgentId, CancellationToken cancellationToken = default)
            => new(new CapabilityMap(coordinatorAgentId, [
                new SubAgentCapability("reviewer", "code-reviewer",
                    Description: "OVERLAID: Reviews code with the team's house style.",
                    Tags: ["role:senior"],
                    Mode: LocalAgentInvocationMode.Blocking),
            ]));
        public void Invalidate(string coordinatorAgentId) { }
    }

    private sealed class ReturnsEmptyDescriptionBuilder : IAgentCapabilityMapBuilder
    {
        public ValueTask<CapabilityMap> BuildAsync(string coordinatorAgentId, CancellationToken cancellationToken = default)
            => new(new CapabilityMap(coordinatorAgentId, [
                new SubAgentCapability("reviewer", "code-reviewer",
                    Description: null,
                    Tags: [],
                    Mode: LocalAgentInvocationMode.Blocking),
            ]));
        public void Invalidate(string coordinatorAgentId) { }
    }
}
