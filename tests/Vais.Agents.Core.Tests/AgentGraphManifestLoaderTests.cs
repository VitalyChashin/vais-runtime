// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using FluentAssertions;
using Vais.Agents.Control;
using Vais.Agents.Control.Manifests;
using Xunit;

namespace Vais.Agents.Core.Tests;

/// <summary>
/// v0.9 PR 2: YAML / JSON manifest loader for <c>kind: AgentGraph</c>. Exercises
/// both archetype YAMLs from the spike, envelope round-trip, validation error
/// paths, and polymorphic multi-kind streams.
/// </summary>
public sealed class AgentGraphManifestLoaderTests
{
    [Fact]
    public async Task Archetype_A_Pure_Handoff_Parses_Cleanly_From_Yaml()
    {
        var yaml = """
            apiVersion: vais.agents/v1
            kind: AgentGraph
            metadata:
              id: customer-router
              version: "1.0"
              description: Route customer queries to the right specialist agent.
            spec:
              entry: triage
              nodes:
                - id: triage
                  kind: Agent
                  ref: { id: triage-agent, version: "1.0" }
                - id: billing
                  kind: Agent
                  ref: { id: billing-agent, version: "1.0" }
                - id: sales
                  kind: Agent
                  ref: { id: sales-agent, version: "1.0" }
                - id: end
                  kind: End
              edges:
                - from: triage
                  to: billing
                  when:
                    property: lastMessage.Text
                    operator: Contains
                    value: refund
                - from: triage
                  to: sales
                  when:
                    property: lastMessage.Text
                    operator: Contains
                    value: upgrade
                - from: triage
                  to: end
                  when: always
                - from: billing
                  to: end
                - from: sales
                  to: end
            """;

        var loader = new YamlAgentGraphManifestLoader();
        var graphs = await loader.LoadFromStringAsync(yaml);

        graphs.Should().ContainSingle();
        var g = graphs[0];
        g.Id.Should().Be("customer-router");
        g.Entry.Should().Be("triage");
        g.Nodes.Should().HaveCount(4);
        g.Edges.Should().HaveCount(5);

        var refundEdge = g.Edges.Single(e => e.From == "triage" && e.To == "billing");
        var predicate = refundEdge.When.Should().BeOfType<GraphEdgePredicate.PropertyMatcher>().Subject;
        predicate.Property.Should().Be("lastMessage.Text");
        predicate.Operator.Should().Be(GraphPredicateOperator.Contains);
        predicate.Value!.Value.GetString().Should().Be("refund");
    }

    [Fact]
    public async Task Archetype_B_Retrieval_Loop_Parses_Combinators_And_Effects()
    {
        var yaml = """
            apiVersion: vais.agents/v1
            kind: AgentGraph
            metadata:
              id: reflective-qa
              version: "1.0"
            spec:
              entry: retrieve
              state:
                schema:
                  type: object
                  properties:
                    query: { type: string }
                    quality: { type: number, minimum: 0, maximum: 1 }
                    retryCount: { type: integer }
              nodes:
                - id: retrieve
                  kind: Agent
                  ref: { id: retrieval-agent, version: "1.0" }
                  stateBindings:
                    input: [query, retryCount]
                    output: [docs]
                - id: answer
                  kind: Agent
                  ref: { id: answering-agent, version: "1.0" }
                - id: grade
                  kind: Agent
                  ref: { id: grading-agent, version: "1.0" }
                  stateBindings:
                    output: [quality]
                - id: end
                  kind: End
              edges:
                - from: retrieve
                  to: answer
                - from: answer
                  to: grade
                - from: grade
                  to: retrieve
                  when:
                    allOf:
                      - { property: quality, operator: Lt, value: 0.7 }
                      - { property: retryCount, operator: Lt, value: 3 }
                  onTraverse:
                    increment: { property: retryCount }
                - from: grade
                  to: end
              maxSteps: 50
            """;

        var loader = new YamlAgentGraphManifestLoader();
        var graphs = await loader.LoadFromStringAsync(yaml);

        graphs.Should().ContainSingle();
        var g = graphs[0];
        g.MaxSteps.Should().Be(50);
        g.StateSchema.Should().NotBeNull();

        var loopEdge = g.Edges.Single(e => e.From == "grade" && e.To == "retrieve");
        var allOf = loopEdge.When.Should().BeOfType<GraphEdgePredicate.AllOf>().Subject;
        allOf.Predicates.Should().HaveCount(2);
        var increment = loopEdge.OnTraverse.Should().BeOfType<GraphEdgeEffect.Increment>().Subject;
        increment.Property.Should().Be("retryCount");
        increment.By.Should().Be(1);

        var retrieve = g.Nodes.Single(n => n.Id == "retrieve");
        retrieve.StateBindings!.Input.Should().BeEquivalentTo("query", "retryCount");
        retrieve.StateBindings.Output.Should().BeEquivalentTo("docs");
    }

    [Fact]
    public async Task Envelope_JSON_Round_Trips_Back_To_Equivalent_Manifest()
    {
        var original = new AgentGraphManifest(
            Id: "round-trip", Version: "1.0", Entry: "a",
            Nodes: new[]
            {
                new GraphNode("a", "Agent", Ref: new GraphAgentRef("agent-a", "1.0"),
                    StateBindings: new GraphStateBindings(Input: new[] { "q" }, Output: new[] { "r" })),
                new GraphNode("b", "Interrupt", InterruptReason: "needs approval"),
                new GraphNode("end", "End"),
            },
            Edges: new[]
            {
                new GraphEdge("a", "b",
                    When: new GraphEdgePredicate.PropertyMatcher("score", GraphPredicateOperator.Gte, JsonSerializer.SerializeToElement(0.5)),
                    OnTraverse: new GraphEdgeEffect.Increment("attempts", 2)),
                new GraphEdge("b", "end"),
            })
        { MaxSteps = 10 };

        var envelope = AgentGraphManifestEnvelope.Serialize(original);
        var loader = new JsonAgentGraphManifestLoader();
        var roundTripped = await loader.LoadFromStringAsync(envelope);

        roundTripped.Should().ContainSingle();
        var r = roundTripped[0];
        r.Id.Should().Be(original.Id);
        r.Entry.Should().Be(original.Entry);
        r.MaxSteps.Should().Be(10);
        r.Nodes.Should().HaveCount(3);
        r.Nodes[0].Ref!.Id.Should().Be("agent-a");
        r.Nodes[0].StateBindings!.Input.Should().BeEquivalentTo("q");
        r.Nodes[1].InterruptReason.Should().Be("needs approval");

        var edgeA = r.Edges[0];
        var matcher = edgeA.When.Should().BeOfType<GraphEdgePredicate.PropertyMatcher>().Subject;
        matcher.Property.Should().Be("score");
        matcher.Operator.Should().Be(GraphPredicateOperator.Gte);
        matcher.Value!.Value.GetDouble().Should().BeApproximately(0.5, 1e-9);

        var increment = edgeA.OnTraverse.Should().BeOfType<GraphEdgeEffect.Increment>().Subject;
        increment.Property.Should().Be("attempts");
        increment.By.Should().Be(2);
    }

    [Fact]
    public async Task Missing_Entry_Node_Throws_Validation()
    {
        var yaml = """
            apiVersion: vais.agents/v1
            kind: AgentGraph
            metadata: { id: bad, version: "1.0" }
            spec:
              entry: ghost
              nodes:
                - id: a
                  kind: End
              edges: []
            """;

        var loader = new YamlAgentGraphManifestLoader();
        var ex = await FluentActions.Invoking(async () => await loader.LoadFromStringAsync(yaml))
            .Should().ThrowAsync<AgentManifestValidationException>();
        ex.Which.Errors.Should().Contain(e => e.Contains("entry node 'ghost' not found"));
    }

    [Fact]
    public async Task Edge_To_Nonexistent_Node_Throws_Validation()
    {
        var yaml = """
            apiVersion: vais.agents/v1
            kind: AgentGraph
            metadata: { id: bad, version: "1.0" }
            spec:
              entry: a
              nodes:
                - id: a
                  kind: Agent
                  ref: { id: x }
                - id: end
                  kind: End
              edges:
                - from: a
                  to: ghost
            """;

        var loader = new YamlAgentGraphManifestLoader();
        var ex = await FluentActions.Invoking(async () => await loader.LoadFromStringAsync(yaml))
            .Should().ThrowAsync<AgentManifestValidationException>();
        ex.Which.Errors.Should().Contain(e => e.Contains("unknown 'to' node 'ghost'"));
    }

    [Fact]
    public async Task Cycle_Without_MaxSteps_Throws_Validation()
    {
        var yaml = """
            apiVersion: vais.agents/v1
            kind: AgentGraph
            metadata: { id: runaway, version: "1.0" }
            spec:
              entry: a
              nodes:
                - id: a
                  kind: Agent
                  ref: { id: x }
                - id: b
                  kind: Agent
                  ref: { id: y }
              edges:
                - from: a
                  to: b
                - from: b
                  to: a
            """;

        var loader = new YamlAgentGraphManifestLoader();
        var ex = await FluentActions.Invoking(async () => await loader.LoadFromStringAsync(yaml))
            .Should().ThrowAsync<AgentManifestValidationException>();
        ex.Which.Errors.Should().Contain(e => e.Contains("cycle") && e.Contains("maxSteps"));
    }

    [Fact]
    public async Task Polymorphic_Loader_Returns_Mixed_Agent_And_Graph_Resources()
    {
        var yaml = """
            apiVersion: vais.agents/v1
            kind: Agent
            metadata: { id: echo, version: "1.0" }
            spec:
              systemPrompt: { inline: "Echo back." }
            ---
            apiVersion: vais.agents/v1
            kind: AgentGraph
            metadata: { id: line, version: "1.0" }
            spec:
              entry: start
              nodes:
                - id: start
                  kind: Agent
                  ref: { id: echo, version: "1.0" }
                - id: end
                  kind: End
              edges:
                - from: start
                  to: end
            """;

        var loader = new YamlAgentGraphManifestLoader();
        var resources = await loader.LoadAllResourcesFromStringAsync(yaml);

        resources.Should().HaveCount(2);
        resources[0].Should().BeOfType<ManifestResource.AgentCase>()
            .Which.Manifest.Id.Should().Be("echo");
        resources[1].Should().BeOfType<ManifestResource.AgentGraphCase>()
            .Which.Graph.Id.Should().Be("line");
    }
}
