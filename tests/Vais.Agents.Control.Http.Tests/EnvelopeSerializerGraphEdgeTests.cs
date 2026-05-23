// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using FluentAssertions;
using Vais.Agents.Control.Manifests;
using Xunit;

namespace Vais.Agents.Control.Http.Tests;

/// <summary>
/// Round-trip tests for <see cref="EnvelopeSerializer"/> + <see cref="JsonAgentGraphManifestLoader"/>.
/// Every <see cref="GraphEdgePredicate"/> and <see cref="GraphEdgeEffect"/> subtype must survive
/// Serialize → LoadFromStringAsync without field loss.
/// </summary>
public sealed class EnvelopeSerializerGraphEdgeTests
{
    private static readonly GraphNode[] Nodes =
    {
        new GraphNode("start", "Agent", Ref: new GraphAgentRef("start-agent")),
        new GraphNode("finish", "End"),
    };

    private static AgentGraphManifest ManifestWithEdge(GraphEdgePredicate? when, GraphEdgeEffect? onTraverse) =>
        new(Id: "test-graph", Version: "1.0", Entry: "start", Nodes: Nodes,
            Edges: new[] { new GraphEdge("start", "finish", when, onTraverse) });

    // ── Predicate round-trips ──────────────────────────────────────────────────

    public static IEnumerable<object[]> PredicateCases()
    {
        yield return new object[] { new GraphEdgePredicate.Always() };
        yield return new object[] { new GraphEdgePredicate.Expression("=Local.retryCount < 3") };
        yield return new object[] { new GraphEdgePredicate.PropertyMatcher("status", GraphPredicateOperator.Eq, JsonDocument.Parse("\"done\"").RootElement.Clone()) };
        yield return new object[] { new GraphEdgePredicate.PropertyMatcher("count", GraphPredicateOperator.Gt, JsonDocument.Parse("3").RootElement.Clone()) };
        yield return new object[] { new GraphEdgePredicate.PropertyMatcher("tags", GraphPredicateOperator.Contains, JsonDocument.Parse("\"urgent\"").RootElement.Clone()) };
        yield return new object[] { new GraphEdgePredicate.PropertyMatcher("result", GraphPredicateOperator.Exists, null) };
        yield return new object[] { new GraphEdgePredicate.AllOf(new GraphEdgePredicate[] { new GraphEdgePredicate.Always(), new GraphEdgePredicate.Expression("=Local.x > 0") }) };
        yield return new object[] { new GraphEdgePredicate.AnyOf(new GraphEdgePredicate[] { new GraphEdgePredicate.Always() }) };
        yield return new object[] { new GraphEdgePredicate.Not(new GraphEdgePredicate.Always()) };
        yield return new object[] { new GraphEdgePredicate.HandlerRef(new GraphHandlerRef("MyPredicate", "My.Assembly")) };
    }

    [Theory]
    [MemberData(nameof(PredicateCases))]
    public async Task Serialize_DropsNoFields_AcrossEveryPredicateSubtype(GraphEdgePredicate predicate)
    {
        var json = EnvelopeSerializer.Serialize(ManifestWithEdge(predicate, null));
        var manifests = await new JsonAgentGraphManifestLoader().LoadFromStringAsync(json);
        var edge = manifests.Single().Edges.Single();

        JsonSerializer.Serialize(edge.When).Should().Be(JsonSerializer.Serialize(predicate));
    }

    // ── Effect round-trips ────────────────────────────────────────────────────

    public static IEnumerable<object[]> EffectCases()
    {
        yield return new object[] { new GraphEdgeEffect.Set("status", JsonDocument.Parse("\"done\"").RootElement.Clone()) };
        yield return new object[] { new GraphEdgeEffect.Increment("retryCount") };
        yield return new object[] { new GraphEdgeEffect.Increment("retryCount", 3) };
        yield return new object[] { new GraphEdgeEffect.Append("messages", JsonDocument.Parse("\"hello\"").RootElement.Clone()) };
        yield return new object[] { new GraphEdgeEffect.HandlerRef(new GraphHandlerRef("MyEffect", "My.Assembly")) };
    }

    [Theory]
    [MemberData(nameof(EffectCases))]
    public async Task Serialize_DropsNoFields_AcrossEveryEffectSubtype(GraphEdgeEffect effect)
    {
        var json = EnvelopeSerializer.Serialize(ManifestWithEdge(null, effect));
        var manifests = await new JsonAgentGraphManifestLoader().LoadFromStringAsync(json);
        var edge = manifests.Single().Edges.Single();

        JsonSerializer.Serialize(edge.OnTraverse).Should().Be(JsonSerializer.Serialize(effect));
    }

    // ── State-reducer round-trips ───────────────────────────────────────────────
    // Client serializer only began emitting stateReducers with the round-trip parity
    // fix; cover every GraphStateReducer subtype through the client path here.

    public static IEnumerable<object[]> ReducerCases()
    {
        yield return new object[] { new GraphStateReducer.LastWriteWins() };
        yield return new object[] { new GraphStateReducer.FirstWriteWins() };
        yield return new object[] { new GraphStateReducer.Append() };
        yield return new object[] { new GraphStateReducer.HandlerRef(new GraphHandlerRef("MyReducer", "My.Assembly")) };
    }

    [Theory]
    [MemberData(nameof(ReducerCases))]
    public async Task Serialize_DropsNoFields_AcrossEveryReducerSubtype(GraphStateReducer reducer)
    {
        var manifest = new AgentGraphManifest(
            Id: "test-graph", Version: "1.0", Entry: "start", Nodes: Nodes,
            Edges: new[] { new GraphEdge("start", "finish") })
        {
            StateReducers = new Dictionary<string, GraphStateReducer> { ["state"] = reducer },
        };

        var json = EnvelopeSerializer.Serialize(manifest);
        var manifests = await new JsonAgentGraphManifestLoader().LoadFromStringAsync(json);
        var roundTripped = manifests.Single().StateReducers!["state"];

        roundTripped.Should().BeOfType(reducer.GetType());
        JsonSerializer.Serialize(roundTripped).Should().Be(JsonSerializer.Serialize(reducer));
    }

    // ── Combined predicate + effect ────────────────────────────────────────────

    [Fact]
    public async Task Serialize_EmittedJsonReparsesIdentically()
    {
        GraphEdgePredicate predicate = new GraphEdgePredicate.Expression("=Local.retryCount < 3");
        GraphEdgeEffect effect = new GraphEdgeEffect.Increment("retryCount");

        var json = EnvelopeSerializer.Serialize(ManifestWithEdge(predicate, effect));
        var manifests = await new JsonAgentGraphManifestLoader().LoadFromStringAsync(json);
        var edge = manifests.Single().Edges.Single();

        JsonSerializer.Serialize(edge.When).Should().Be(JsonSerializer.Serialize(predicate));
        JsonSerializer.Serialize(edge.OnTraverse).Should().Be(JsonSerializer.Serialize(effect));
    }
}
