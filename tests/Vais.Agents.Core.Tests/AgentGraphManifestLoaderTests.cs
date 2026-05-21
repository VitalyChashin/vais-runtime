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
                    docs: { type: array }
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

    // ── v0.20 PR 2: runtimeUrl in ref ──────────────────────────────────────

    [Fact]
    public async Task Ref_RuntimeUrl_Parses_From_Json()
    {
        var json = """
            {
              "apiVersion": "vais.agents/v1",
              "kind": "AgentGraph",
              "metadata": { "id": "cross-rt", "version": "1.0" },
              "spec": {
                "entry": "step",
                "nodes": [
                  { "id": "step", "kind": "Agent",
                    "ref": { "id": "remote-agent", "version": "2.0", "runtimeUrl": "https://runtime-b.svc" } },
                  { "id": "end", "kind": "End" }
                ],
                "edges": [
                  { "from": "step", "to": "end" }
                ]
              }
            }
            """;

        var loader = new JsonAgentGraphManifestLoader();
        var graphs = await loader.LoadFromStringAsync(json);

        graphs.Should().ContainSingle();
        var step = graphs[0].Nodes.Single(n => n.Id == "step");
        step.Ref!.RuntimeUrl.Should().Be("https://runtime-b.svc");
        step.Ref.Version.Should().Be("2.0");
    }

    [Fact]
    public async Task Ref_RuntimeUrl_Parses_From_Yaml()
    {
        var yaml = """
            apiVersion: vais.agents/v1
            kind: AgentGraph
            metadata: { id: cross-rt, version: "1.0" }
            spec:
              entry: step
              nodes:
                - id: step
                  kind: Agent
                  ref: { id: remote-agent, version: "1.0", runtimeUrl: "http://runtime-b.internal" }
                - id: end
                  kind: End
              edges:
                - from: step
                  to: end
            """;

        var loader = new YamlAgentGraphManifestLoader();
        var graphs = await loader.LoadFromStringAsync(yaml);

        graphs.Should().ContainSingle();
        var step = graphs[0].Nodes.Single(n => n.Id == "step");
        step.Ref!.RuntimeUrl.Should().Be("http://runtime-b.internal");
    }

    [Fact]
    public async Task Ref_Without_RuntimeUrl_Yields_Null()
    {
        var yaml = """
            apiVersion: vais.agents/v1
            kind: AgentGraph
            metadata: { id: local-only, version: "1.0" }
            spec:
              entry: step
              nodes:
                - id: step
                  kind: Agent
                  ref: { id: local-agent, version: "1.0" }
                - id: end
                  kind: End
              edges:
                - from: step
                  to: end
            """;

        var loader = new YamlAgentGraphManifestLoader();
        var graphs = await loader.LoadFromStringAsync(yaml);

        graphs[0].Nodes.Single(n => n.Id == "step").Ref!.RuntimeUrl.Should().BeNull();
    }

    [Fact]
    public async Task Ref_InvalidRuntimeUrl_Scheme_Throws_Validation()
    {
        var json = """
            {
              "apiVersion": "vais.agents/v1",
              "kind": "AgentGraph",
              "metadata": { "id": "bad-rt", "version": "1.0" },
              "spec": {
                "entry": "step",
                "nodes": [
                  { "id": "step", "kind": "Agent",
                    "ref": { "id": "agent-x", "runtimeUrl": "grpc://runtime-b.svc" } },
                  { "id": "end", "kind": "End" }
                ],
                "edges": [{ "from": "step", "to": "end" }]
              }
            }
            """;

        var loader = new JsonAgentGraphManifestLoader();
        var ex = await FluentActions.Invoking(async () => await loader.LoadFromStringAsync(json))
            .Should().ThrowAsync<AgentManifestValidationException>();
        ex.Which.Errors.Should().Contain(e => e.Contains("runtimeUrl") && e.Contains("http or https"));
    }

    [Fact]
    public async Task Ref_RelativeRuntimeUrl_Throws_Validation()
    {
        var json = """
            {
              "apiVersion": "vais.agents/v1",
              "kind": "AgentGraph",
              "metadata": { "id": "bad-rt", "version": "1.0" },
              "spec": {
                "entry": "step",
                "nodes": [
                  { "id": "step", "kind": "Agent",
                    "ref": { "id": "agent-x", "runtimeUrl": "/relative/path" } },
                  { "id": "end", "kind": "End" }
                ],
                "edges": [{ "from": "step", "to": "end" }]
              }
            }
            """;

        var loader = new JsonAgentGraphManifestLoader();
        var ex = await FluentActions.Invoking(async () => await loader.LoadFromStringAsync(json))
            .Should().ThrowAsync<AgentManifestValidationException>();
        ex.Which.Errors.Should().Contain(e => e.Contains("runtimeUrl") && e.Contains("http or https"));
    }

    [Fact]
    public async Task Envelope_RoundTrip_Preserves_RuntimeUrl()
    {
        var original = new AgentGraphManifest(
            Id: "rt-round-trip", Version: "1.0", Entry: "step",
            Nodes: new[]
            {
                new GraphNode("step", "Agent", Ref: new GraphAgentRef("remote-agent", "2.0", "https://runtime-b.svc")),
                new GraphNode("end", "End"),
            },
            Edges: new[] { new GraphEdge("step", "end") });

        var envelope = AgentGraphManifestEnvelope.Serialize(original);
        var loader = new JsonAgentGraphManifestLoader();
        var roundTripped = await loader.LoadFromStringAsync(envelope);

        roundTripped.Should().ContainSingle();
        roundTripped[0].Nodes.Single(n => n.Id == "step").Ref!.RuntimeUrl
            .Should().Be("https://runtime-b.svc");
    }

    [Fact]
    public async Task Envelope_RoundTrip_Preserves_A2AUrl()
    {
        var original = new AgentGraphManifest(
            Id: "a2a-round-trip", Version: "1.0", Entry: "step",
            Nodes: new[]
            {
                new GraphNode("step", "Agent", Ref: new GraphAgentRef("external-agent", "1.0", A2AUrl: "https://enricher.vendor-a.com")),
                new GraphNode("end", "End"),
            },
            Edges: new[] { new GraphEdge("step", "end") });

        var envelope = AgentGraphManifestEnvelope.Serialize(original);
        var loader = new JsonAgentGraphManifestLoader();
        var roundTripped = await loader.LoadFromStringAsync(envelope);

        var step = roundTripped.Single().Nodes.Single(n => n.Id == "step");
        step.Ref!.A2AUrl.Should().Be("https://enricher.vendor-a.com");
        step.Ref.RuntimeUrl.Should().BeNull();
    }

    // ── v0.21: a2aUrl in ref ─────────────────────────────────────────────

    [Fact]
    public async Task Ref_A2AUrl_Parses_From_Yaml()
    {
        var yaml = """
            apiVersion: vais.agents/v1
            kind: AgentGraph
            metadata: { id: a2a-graph, version: "1.0" }
            spec:
              entry: step
              nodes:
                - id: step
                  kind: Agent
                  ref: { id: external-agent, version: "1.0", a2aUrl: "https://enricher.vendor-a.com" }
                - id: end
                  kind: End
              edges:
                - from: step
                  to: end
            """;

        var loader = new YamlAgentGraphManifestLoader();
        var graphs = await loader.LoadFromStringAsync(yaml);

        graphs.Should().ContainSingle();
        var step = graphs[0].Nodes.Single(n => n.Id == "step");
        step.Ref!.A2AUrl.Should().Be("https://enricher.vendor-a.com");
        step.Ref.RuntimeUrl.Should().BeNull();
    }

    [Fact]
    public async Task Ref_A2AUrl_Parses_From_Json()
    {
        var json = """
            {
              "apiVersion": "vais.agents/v1",
              "kind": "AgentGraph",
              "metadata": { "id": "a2a-json", "version": "1.0" },
              "spec": {
                "entry": "step",
                "nodes": [
                  { "id": "step", "kind": "Agent",
                    "ref": { "id": "ext-agent", "a2aUrl": "https://a2a.example.com/agent" } },
                  { "id": "end", "kind": "End" }
                ],
                "edges": [{ "from": "step", "to": "end" }]
              }
            }
            """;

        var loader = new JsonAgentGraphManifestLoader();
        var graphs = await loader.LoadFromStringAsync(json);

        graphs.Should().ContainSingle();
        var step = graphs[0].Nodes.Single(n => n.Id == "step");
        step.Ref!.A2AUrl.Should().Be("https://a2a.example.com/agent");
    }

    [Fact]
    public async Task Ref_A2AUrl_InvalidScheme_Throws_Validation()
    {
        var json = """
            {
              "apiVersion": "vais.agents/v1",
              "kind": "AgentGraph",
              "metadata": { "id": "bad-a2a", "version": "1.0" },
              "spec": {
                "entry": "step",
                "nodes": [
                  { "id": "step", "kind": "Agent",
                    "ref": { "id": "agent-x", "a2aUrl": "ftp://bad.example.com" } },
                  { "id": "end", "kind": "End" }
                ],
                "edges": [{ "from": "step", "to": "end" }]
              }
            }
            """;

        var loader = new JsonAgentGraphManifestLoader();
        var ex = await FluentActions.Invoking(async () => await loader.LoadFromStringAsync(json))
            .Should().ThrowAsync<AgentManifestValidationException>();
        ex.Which.Errors.Should().Contain(e => e.Contains("a2aUrl") && e.Contains("http or https"));
    }

    [Fact]
    public async Task Ref_A2AUrl_And_RuntimeUrl_BothSet_Throws_Validation()
    {
        var json = """
            {
              "apiVersion": "vais.agents/v1",
              "kind": "AgentGraph",
              "metadata": { "id": "conflict", "version": "1.0" },
              "spec": {
                "entry": "step",
                "nodes": [
                  { "id": "step", "kind": "Agent",
                    "ref": { "id": "agent-x", "runtimeUrl": "https://rt.svc", "a2aUrl": "https://a2a.svc" } },
                  { "id": "end", "kind": "End" }
                ],
                "edges": [{ "from": "step", "to": "end" }]
              }
            }
            """;

        var loader = new JsonAgentGraphManifestLoader();
        var ex = await FluentActions.Invoking(async () => await loader.LoadFromStringAsync(json))
            .Should().ThrowAsync<AgentManifestValidationException>();
        ex.Which.Errors.Should().Contain(e => e.Contains("mutually exclusive"));
    }

    [Fact]
    public async Task Ref_Without_A2AUrl_Yields_Null()
    {
        var yaml = """
            apiVersion: vais.agents/v1
            kind: AgentGraph
            metadata: { id: local-only, version: "1.0" }
            spec:
              entry: step
              nodes:
                - id: step
                  kind: Agent
                  ref: { id: local-agent, version: "1.0" }
                - id: end
                  kind: End
              edges:
                - from: step
                  to: end
            """;

        var loader = new YamlAgentGraphManifestLoader();
        var graphs = await loader.LoadFromStringAsync(yaml);

        graphs[0].Nodes.Single(n => n.Id == "step").Ref!.A2AUrl.Should().BeNull();
    }

    // ── v0.37 custom declarable reducers ─────────────────────────────────────

    [Fact]
    public async Task StateReducers_LastWriteWins_And_Append_Parse_From_Yaml()
    {
        var yaml = """
            apiVersion: vais.agents/v1
            kind: AgentGraph
            metadata: { id: reducer-graph, version: "1.0" }
            spec:
              entry: step
              nodes:
                - id: step
                  kind: Agent
                  ref: { id: agent-x }
                - id: end
                  kind: End
              edges:
                - from: step
                  to: end
              stateReducers:
                score: lastWriteWins
                tags: append
            """;

        var loader = new YamlAgentGraphManifestLoader();
        var graphs = await loader.LoadFromStringAsync(yaml);

        graphs.Should().ContainSingle();
        var g = graphs[0];
        g.StateReducers.Should().NotBeNull();
        g.StateReducers!["score"].Should().BeOfType<GraphStateReducer.LastWriteWins>();
        g.StateReducers["tags"].Should().BeOfType<GraphStateReducer.Append>();
    }

    [Fact]
    public async Task StateReducers_HandlerRef_Parses_From_Json()
    {
        var json = """
            {
              "apiVersion": "vais.agents/v1",
              "kind": "AgentGraph",
              "metadata": { "id": "custom-reducer", "version": "1.0" },
              "spec": {
                "entry": "step",
                "nodes": [
                  { "id": "step", "kind": "Agent", "ref": { "id": "agent-x" } },
                  { "id": "end", "kind": "End" }
                ],
                "edges": [{ "from": "step", "to": "end" }],
                "stateReducers": {
                  "score": { "handlerRef": { "typeName": "MyApp.ScoreReducer", "assemblyName": "MyApp" } }
                }
              }
            }
            """;

        var loader = new JsonAgentGraphManifestLoader();
        var graphs = await loader.LoadFromStringAsync(json);

        graphs.Should().ContainSingle();
        var reducer = graphs[0].StateReducers!["score"].Should().BeOfType<GraphStateReducer.HandlerRef>().Subject;
        reducer.Handler.TypeName.Should().Be("MyApp.ScoreReducer");
        reducer.Handler.AssemblyName.Should().Be("MyApp");
    }

    [Fact]
    public async Task Envelope_StateReducers_Round_Trips()
    {
        var original = new AgentGraphManifest(
            Id: "reducer-rt", Version: "1.0", Entry: "step",
            Nodes: new[]
            {
                new GraphNode("step", "Agent", Ref: new GraphAgentRef("agent-x")),
                new GraphNode("end", "End"),
            },
            Edges: new[] { new GraphEdge("step", "end") })
        {
            StateReducers = new Dictionary<string, GraphStateReducer>
            {
                ["tags"] = new GraphStateReducer.Append(),
                ["score"] = new GraphStateReducer.LastWriteWins(),
                ["custom"] = new GraphStateReducer.HandlerRef(new GraphHandlerRef("My.Reducer", "My.Assembly")),
            },
        };

        var envelope = AgentGraphManifestEnvelope.Serialize(original);
        var loader = new JsonAgentGraphManifestLoader();
        var roundTripped = await loader.LoadFromStringAsync(envelope);

        roundTripped.Should().ContainSingle();
        var rt = roundTripped[0];
        rt.StateReducers.Should().NotBeNull();
        rt.StateReducers!["tags"].Should().BeOfType<GraphStateReducer.Append>();
        rt.StateReducers["score"].Should().BeOfType<GraphStateReducer.LastWriteWins>();
        var hrReducer = rt.StateReducers["custom"].Should().BeOfType<GraphStateReducer.HandlerRef>().Subject;
        hrReducer.Handler.TypeName.Should().Be("My.Reducer");
        hrReducer.Handler.AssemblyName.Should().Be("My.Assembly");
    }

    [Fact]
    public async Task Validator_Rejects_Reducer_Key_Not_In_StateSchema()
    {
        var yaml = """
            apiVersion: vais.agents/v1
            kind: AgentGraph
            metadata: { id: bad-reducer, version: "1.0" }
            spec:
              entry: step
              state:
                schema:
                  type: object
                  properties:
                    score: { type: number }
              nodes:
                - id: step
                  kind: Agent
                  ref: { id: agent-x }
                - id: end
                  kind: End
              edges:
                - from: step
                  to: end
              maxSteps: 10
              stateReducers:
                tags: append
            """;

        var loader = new YamlAgentGraphManifestLoader();
        var ex = await FluentActions.Invoking(async () => await loader.LoadFromStringAsync(yaml))
            .Should().ThrowAsync<AgentManifestValidationException>();
        ex.Which.Errors.Should().Contain(e => e.Contains("tags") && e.Contains("stateReducers"));
    }

    // ── v0.39 structural validator cross-checks ───────────────────────────────

    [Fact]
    public async Task Validator_Rejects_Code_Node_With_Empty_HandlerRef_TypeName()
    {
        // Empty typeName is caught by the loader's parse path (reports "required").
        var json = """
            {
              "apiVersion": "vais.agents/v1",
              "kind": "AgentGraph",
              "metadata": { "id": "bad-handler", "version": "1.0" },
              "spec": {
                "entry": "step",
                "nodes": [
                  { "id": "step", "kind": "Code", "handlerRef": { "typeName": "" } },
                  { "id": "end", "kind": "End" }
                ],
                "edges": [{ "from": "step", "to": "end" }]
              }
            }
            """;

        var loader = new JsonAgentGraphManifestLoader();
        var ex = await FluentActions.Invoking(async () => await loader.LoadFromStringAsync(json))
            .Should().ThrowAsync<AgentManifestValidationException>();
        ex.Which.Errors.Should().Contain(e => e.Contains("typeName") && e.Contains("required"));
    }

    [Fact]
    public async Task Validator_Rejects_Code_Node_With_Whitespace_In_HandlerRef_TypeName()
    {
        var json = """
            {
              "apiVersion": "vais.agents/v1",
              "kind": "AgentGraph",
              "metadata": { "id": "spaced-handler", "version": "1.0" },
              "spec": {
                "entry": "step",
                "nodes": [
                  { "id": "step", "kind": "Code", "handlerRef": { "typeName": "My Handler" } },
                  { "id": "end", "kind": "End" }
                ],
                "edges": [{ "from": "step", "to": "end" }]
              }
            }
            """;

        var loader = new JsonAgentGraphManifestLoader();
        var ex = await FluentActions.Invoking(async () => await loader.LoadFromStringAsync(json))
            .Should().ThrowAsync<AgentManifestValidationException>();
        ex.Which.Errors.Should().Contain(e => e.Contains("typeName") && e.Contains("invalid"));
    }

    [Fact]
    public async Task Validator_Rejects_Edge_Predicate_HandlerRef_With_Empty_TypeName()
    {
        // Empty typeName is caught by the loader's parse path (reports "required").
        var json = """
            {
              "apiVersion": "vais.agents/v1",
              "kind": "AgentGraph",
              "metadata": { "id": "bad-pred", "version": "1.0" },
              "spec": {
                "entry": "step",
                "nodes": [
                  { "id": "step", "kind": "Agent", "ref": { "id": "agent-x" } },
                  { "id": "end", "kind": "End" }
                ],
                "edges": [
                  { "from": "step", "to": "end",
                    "when": { "handlerRef": { "typeName": "" } } }
                ]
              }
            }
            """;

        var loader = new JsonAgentGraphManifestLoader();
        var ex = await FluentActions.Invoking(async () => await loader.LoadFromStringAsync(json))
            .Should().ThrowAsync<AgentManifestValidationException>();
        ex.Which.Errors.Should().Contain(e => e.Contains("typeName") && e.Contains("required"));
    }

    [Fact]
    public async Task Validator_Rejects_Nested_Predicate_HandlerRef_With_Empty_TypeName()
    {
        // HandlerRef inside an allOf combinator — tests recursive predicate walker.
        // Empty typeName caught by the loader's parse path (reports "required").
        var json = """
            {
              "apiVersion": "vais.agents/v1",
              "kind": "AgentGraph",
              "metadata": { "id": "nested-pred", "version": "1.0" },
              "spec": {
                "entry": "step",
                "nodes": [
                  { "id": "step", "kind": "Agent", "ref": { "id": "agent-x" } },
                  { "id": "end", "kind": "End" }
                ],
                "edges": [
                  { "from": "step", "to": "end",
                    "when": {
                      "allOf": [
                        { "property": "score", "operator": "Gt", "value": 0.5 },
                        { "handlerRef": { "typeName": "" } }
                      ]
                    } }
                ],
                "maxSteps": 5
              }
            }
            """;

        var loader = new JsonAgentGraphManifestLoader();
        var ex = await FluentActions.Invoking(async () => await loader.LoadFromStringAsync(json))
            .Should().ThrowAsync<AgentManifestValidationException>();
        ex.Which.Errors.Should().Contain(e => e.Contains("typeName") && e.Contains("required"));
    }

    [Fact]
    public async Task Validator_Rejects_Nested_Predicate_HandlerRef_With_Embedded_Whitespace_TypeName()
    {
        // HandlerRef inside an allOf combinator with whitespace-containing TypeName.
        // The loader's IsNullOrEmpty check passes for "My Bad Handler" so the validator
        // catches it after parsing (reports "invalid").
        var json = """
            {
              "apiVersion": "vais.agents/v1",
              "kind": "AgentGraph",
              "metadata": { "id": "nested-pred-ws", "version": "1.0" },
              "spec": {
                "entry": "step",
                "nodes": [
                  { "id": "step", "kind": "Agent", "ref": { "id": "agent-x" } },
                  { "id": "end", "kind": "End" }
                ],
                "edges": [
                  { "from": "step", "to": "end",
                    "when": {
                      "allOf": [
                        { "property": "score", "operator": "Gt", "value": 0.5 },
                        { "handlerRef": { "typeName": "My Bad Handler" } }
                      ]
                    } }
                ],
                "maxSteps": 5
              }
            }
            """;

        var loader = new JsonAgentGraphManifestLoader();
        var ex = await FluentActions.Invoking(async () => await loader.LoadFromStringAsync(json))
            .Should().ThrowAsync<AgentManifestValidationException>();
        ex.Which.Errors.Should().Contain(e => e.Contains("typeName") && e.Contains("invalid"));
    }

    [Fact]
    public async Task Validator_Rejects_Edge_Effect_HandlerRef_With_Empty_TypeName()
    {
        var json = """
            {
              "apiVersion": "vais.agents/v1",
              "kind": "AgentGraph",
              "metadata": { "id": "bad-effect", "version": "1.0" },
              "spec": {
                "entry": "step",
                "nodes": [
                  { "id": "step", "kind": "Agent", "ref": { "id": "agent-x" } },
                  { "id": "end", "kind": "End" }
                ],
                "edges": [
                  { "from": "step", "to": "end",
                    "onTraverse": { "handlerRef": { "typeName": "  " } } }
                ]
              }
            }
            """;

        var loader = new JsonAgentGraphManifestLoader();
        var ex = await FluentActions.Invoking(async () => await loader.LoadFromStringAsync(json))
            .Should().ThrowAsync<AgentManifestValidationException>();
        ex.Which.Errors.Should().Contain(e => e.Contains("typeName") && e.Contains("invalid"));
    }

    [Fact]
    public async Task Validator_Rejects_StateReducer_HandlerRef_With_Empty_TypeName()
    {
        // Empty typeName is caught by the loader's parse path (reports "required").
        var json = """
            {
              "apiVersion": "vais.agents/v1",
              "kind": "AgentGraph",
              "metadata": { "id": "bad-reducer-hr", "version": "1.0" },
              "spec": {
                "entry": "step",
                "nodes": [
                  { "id": "step", "kind": "Agent", "ref": { "id": "agent-x" } },
                  { "id": "end", "kind": "End" }
                ],
                "edges": [{ "from": "step", "to": "end" }],
                "stateReducers": {
                  "score": { "handlerRef": { "typeName": "" } }
                }
              }
            }
            """;

        var loader = new JsonAgentGraphManifestLoader();
        var ex = await FluentActions.Invoking(async () => await loader.LoadFromStringAsync(json))
            .Should().ThrowAsync<AgentManifestValidationException>();
        ex.Which.Errors.Should().Contain(e => e.Contains("typeName") && e.Contains("required"));
    }

    [Fact]
    public async Task Validator_Rejects_StateReducer_HandlerRef_With_Whitespace_TypeName()
    {
        // Whitespace-only typeName passes the loader's IsNullOrEmpty check but is
        // caught by the validator (reports "invalid").
        var json = """
            {
              "apiVersion": "vais.agents/v1",
              "kind": "AgentGraph",
              "metadata": { "id": "ws-reducer-hr", "version": "1.0" },
              "spec": {
                "entry": "step",
                "nodes": [
                  { "id": "step", "kind": "Agent", "ref": { "id": "agent-x" } },
                  { "id": "end", "kind": "End" }
                ],
                "edges": [{ "from": "step", "to": "end" }],
                "stateReducers": {
                  "score": { "handlerRef": { "typeName": "   " } }
                }
              }
            }
            """;

        var loader = new JsonAgentGraphManifestLoader();
        var ex = await FluentActions.Invoking(async () => await loader.LoadFromStringAsync(json))
            .Should().ThrowAsync<AgentManifestValidationException>();
        ex.Which.Errors.Should().Contain(e => e.Contains("typeName") && e.Contains("invalid"));
    }

    [Fact]
    public async Task Validator_Rejects_StateBindings_Input_Key_Not_In_StateSchema()
    {
        var yaml = """
            apiVersion: vais.agents/v1
            kind: AgentGraph
            metadata: { id: bad-input-binding, version: "1.0" }
            spec:
              entry: step
              state:
                schema:
                  type: object
                  properties:
                    query: { type: string }
              nodes:
                - id: step
                  kind: Agent
                  ref: { id: agent-x }
                  stateBindings:
                    input: [query, undeclaredKey]
                - id: end
                  kind: End
              edges:
                - from: step
                  to: end
            """;

        var loader = new YamlAgentGraphManifestLoader();
        var ex = await FluentActions.Invoking(async () => await loader.LoadFromStringAsync(yaml))
            .Should().ThrowAsync<AgentManifestValidationException>();
        ex.Which.Errors.Should().Contain(e => e.Contains("undeclaredKey") && e.Contains("stateBindings.input"));
    }

    [Fact]
    public async Task Validator_Rejects_StateBindings_Output_Key_Not_In_StateSchema()
    {
        var yaml = """
            apiVersion: vais.agents/v1
            kind: AgentGraph
            metadata: { id: bad-output-binding, version: "1.0" }
            spec:
              entry: step
              state:
                schema:
                  type: object
                  properties:
                    result: { type: string }
              nodes:
                - id: step
                  kind: Agent
                  ref: { id: agent-x }
                  stateBindings:
                    output: [result, ghostField]
                - id: end
                  kind: End
              edges:
                - from: step
                  to: end
            """;

        var loader = new YamlAgentGraphManifestLoader();
        var ex = await FluentActions.Invoking(async () => await loader.LoadFromStringAsync(yaml))
            .Should().ThrowAsync<AgentManifestValidationException>();
        ex.Which.Errors.Should().Contain(e => e.Contains("ghostField") && e.Contains("stateBindings.output"));
    }

    [Fact]
    public async Task Validator_Allows_WellKnown_Keys_In_StateBindings_Without_Schema_Declaration()
    {
        // 'messages' and 'lastAssistantText' are well-known runtime keys — exempt even
        // when they are not listed in spec.state.schema.properties.
        var yaml = """
            apiVersion: vais.agents/v1
            kind: AgentGraph
            metadata: { id: well-known-ok, version: "1.0" }
            spec:
              entry: step
              state:
                schema:
                  type: object
                  properties:
                    query: { type: string }
              nodes:
                - id: step
                  kind: Agent
                  ref: { id: agent-x }
                  stateBindings:
                    input: [query, messages]
                    output: [lastAssistantText]
                - id: end
                  kind: End
              edges:
                - from: step
                  to: end
            """;

        var loader = new YamlAgentGraphManifestLoader();
        var graphs = await loader.LoadFromStringAsync(yaml);

        graphs.Should().ContainSingle("well-known keys must not trigger a validation error");
    }

    [Fact]
    public async Task Validator_Allows_StateBindings_When_No_StateSchema_Declared()
    {
        // Without a StateSchema the cross-check is skipped entirely — any key is valid.
        var yaml = """
            apiVersion: vais.agents/v1
            kind: AgentGraph
            metadata: { id: no-schema, version: "1.0" }
            spec:
              entry: step
              nodes:
                - id: step
                  kind: Agent
                  ref: { id: agent-x }
                  stateBindings:
                    input: [anyKey]
                    output: [anotherKey]
                - id: end
                  kind: End
              edges:
                - from: step
                  to: end
            """;

        var loader = new YamlAgentGraphManifestLoader();
        var graphs = await loader.LoadFromStringAsync(yaml);

        graphs.Should().ContainSingle("stateBindings cross-check is skipped when no StateSchema is declared");
    }

    // ── v0.40 agent OutputSchema cross-check ─────────────────────────────────

    [Fact]
    public async Task ValidateAgentOutputSchemaBindings_OutputKeyPresent_Passes()
    {
        // Polymorphic stream: agent "enricher" declares outputSchema with "summary"; graph
        // binds stateBindings.output to "summary" — second-pass cross-check must pass.
        var json = """
            [
              {
                "apiVersion": "vais.agents/v1",
                "kind": "Agent",
                "metadata": { "id": "enricher", "version": "1.0" },
                "spec": {
                  "outputSchema": {
                    "type": "object",
                    "properties": { "summary": { "type": "string" } }
                  }
                }
              },
              {
                "apiVersion": "vais.agents/v1",
                "kind": "AgentGraph",
                "metadata": { "id": "enrich-graph", "version": "1.0" },
                "spec": {
                  "entry": "step",
                  "nodes": [
                    { "id": "step", "kind": "Agent",
                      "ref": { "id": "enricher", "version": "1.0" },
                      "stateBindings": { "output": ["summary"] } },
                    { "id": "end", "kind": "End" }
                  ],
                  "edges": [{ "from": "step", "to": "end" }]
                }
              }
            ]
            """;

        var loader = new JsonAgentGraphManifestLoader();
        var graphs = await loader.LoadFromStringAsync(json);

        graphs.Should().ContainSingle("output key 'summary' is present in agent OutputSchema.properties — no error");
    }

    [Fact]
    public async Task ValidateAgentOutputSchemaBindings_OutputKeyMissing_Reports_Error()
    {
        // stateBindings.output key "ghostField" is not declared in agent's OutputSchema.properties.
        var json = """
            [
              {
                "apiVersion": "vais.agents/v1",
                "kind": "Agent",
                "metadata": { "id": "enricher", "version": "1.0" },
                "spec": {
                  "outputSchema": {
                    "type": "object",
                    "properties": { "summary": { "type": "string" } }
                  }
                }
              },
              {
                "apiVersion": "vais.agents/v1",
                "kind": "AgentGraph",
                "metadata": { "id": "bad-binding-graph", "version": "1.0" },
                "spec": {
                  "entry": "step",
                  "nodes": [
                    { "id": "step", "kind": "Agent",
                      "ref": { "id": "enricher", "version": "1.0" },
                      "stateBindings": { "output": ["ghostField"] } },
                    { "id": "end", "kind": "End" }
                  ],
                  "edges": [{ "from": "step", "to": "end" }]
                }
              }
            ]
            """;

        var loader = new JsonAgentGraphManifestLoader();
        var ex = await FluentActions.Invoking(async () => await loader.LoadFromStringAsync(json))
            .Should().ThrowAsync<AgentManifestValidationException>();
        ex.Which.Errors.Should().Contain(e =>
            e.Contains("ghostField") && e.Contains("enricher") && e.Contains("OutputSchema"));
    }

    [Fact]
    public async Task ValidateAgentOutputSchemaBindings_RemoteRef_SkipsCheck()
    {
        // Even though agent "enricher" is in stream and has no "ghostField", the node's
        // ref.runtimeUrl means the agent runs remotely — OutputSchema check is skipped.
        var json = """
            [
              {
                "apiVersion": "vais.agents/v1",
                "kind": "Agent",
                "metadata": { "id": "enricher", "version": "1.0" },
                "spec": {
                  "outputSchema": {
                    "type": "object",
                    "properties": { "summary": { "type": "string" } }
                  }
                }
              },
              {
                "apiVersion": "vais.agents/v1",
                "kind": "AgentGraph",
                "metadata": { "id": "remote-graph", "version": "1.0" },
                "spec": {
                  "entry": "step",
                  "nodes": [
                    { "id": "step", "kind": "Agent",
                      "ref": { "id": "enricher", "version": "1.0", "runtimeUrl": "https://remote.svc" },
                      "stateBindings": { "output": ["ghostField"] } },
                    { "id": "end", "kind": "End" }
                  ],
                  "edges": [{ "from": "step", "to": "end" }]
                }
              }
            ]
            """;

        var loader = new JsonAgentGraphManifestLoader();
        var graphs = await loader.LoadFromStringAsync(json);

        graphs.Should().ContainSingle("remote ref — OutputSchema check must be skipped");
    }

    [Fact]
    public async Task ValidateAgentOutputSchemaBindings_AgentHasNoOutputSchema_SkipsCheck()
    {
        // Agent in same stream has no outputSchema field — check is silently skipped.
        var json = """
            [
              {
                "apiVersion": "vais.agents/v1",
                "kind": "Agent",
                "metadata": { "id": "plain-agent", "version": "1.0" },
                "spec": {}
              },
              {
                "apiVersion": "vais.agents/v1",
                "kind": "AgentGraph",
                "metadata": { "id": "plain-graph", "version": "1.0" },
                "spec": {
                  "entry": "step",
                  "nodes": [
                    { "id": "step", "kind": "Agent",
                      "ref": { "id": "plain-agent", "version": "1.0" },
                      "stateBindings": { "output": ["anyKey"] } },
                    { "id": "end", "kind": "End" }
                  ],
                  "edges": [{ "from": "step", "to": "end" }]
                }
              }
            ]
            """;

        var loader = new JsonAgentGraphManifestLoader();
        var graphs = await loader.LoadFromStringAsync(json);

        graphs.Should().ContainSingle("no OutputSchema declared — check must be skipped entirely");
    }

    [Fact]
    public async Task ValidateAgentOutputSchemaBindings_AgentNotInStream_SkipsCheck()
    {
        // Graph references "external-agent" which is not in this stream — resolver returns
        // null, so no false-positive OutputSchema error is raised.
        var json = """
            {
              "apiVersion": "vais.agents/v1",
              "kind": "AgentGraph",
              "metadata": { "id": "ext-ref-graph", "version": "1.0" },
              "spec": {
                "entry": "step",
                "nodes": [
                  { "id": "step", "kind": "Agent",
                    "ref": { "id": "external-agent", "version": "1.0" },
                    "stateBindings": { "output": ["someKey"] } },
                  { "id": "end", "kind": "End" }
                ],
                "edges": [{ "from": "step", "to": "end" }]
              }
            }
            """;

        var loader = new JsonAgentGraphManifestLoader();
        var graphs = await loader.LoadFromStringAsync(json);

        graphs.Should().ContainSingle("agent not in stream — no false-positive OutputSchema error");
    }

    [Fact]
    public async Task ValidateAgentOutputSchemaBindings_WellKnownKeys_Exempt()
    {
        // "messages" and "lastAssistantText" are runtime-written — exempt from OutputSchema
        // cross-check even when the agent's OutputSchema doesn't list them.
        var json = """
            [
              {
                "apiVersion": "vais.agents/v1",
                "kind": "Agent",
                "metadata": { "id": "chat-agent", "version": "1.0" },
                "spec": {
                  "outputSchema": {
                    "type": "object",
                    "properties": { "answer": { "type": "string" } }
                  }
                }
              },
              {
                "apiVersion": "vais.agents/v1",
                "kind": "AgentGraph",
                "metadata": { "id": "chat-graph", "version": "1.0" },
                "spec": {
                  "entry": "step",
                  "nodes": [
                    { "id": "step", "kind": "Agent",
                      "ref": { "id": "chat-agent", "version": "1.0" },
                      "stateBindings": { "output": ["messages", "lastAssistantText"] } },
                    { "id": "end", "kind": "End" }
                  ],
                  "edges": [{ "from": "step", "to": "end" }]
                }
              }
            ]
            """;

        var loader = new JsonAgentGraphManifestLoader();
        var graphs = await loader.LoadFromStringAsync(json);

        graphs.Should().ContainSingle("well-known keys 'messages' and 'lastAssistantText' are always exempt");
    }

    // ── P3: inline PowerFx expression edge predicates ─────────────────────────

    [Fact]
    public async Task When_EqualSignString_Parses_As_Expression_Predicate()
    {
        var yaml = """
            apiVersion: vais.agents/v1
            kind: AgentGraph
            metadata: { id: pfx-graph, version: "1.0" }
            spec:
              entry: step
              nodes:
                - id: step
                  kind: Agent
                  ref: { id: agent-x }
                - id: end
                  kind: End
              edges:
                - from: step
                  to: end
                  when: "=Not(IsBlank(Local.research_plan))"
            """;

        var loader = new YamlAgentGraphManifestLoader();
        var graphs = await loader.LoadFromStringAsync(yaml);

        var edge = graphs[0].Edges.Single(e => e.From == "step" && e.To == "end");
        var predicate = edge.When.Should().BeOfType<GraphEdgePredicate.Expression>().Subject;
        predicate.Expr.Should().Be("=Not(IsBlank(Local.research_plan))");
    }

    [Fact]
    public async Task When_EqualSignString_Json_Parses_As_Expression_Predicate()
    {
        var json = """
            [
              {
                "apiVersion": "vais.agents/v1",
                "kind": "AgentGraph",
                "metadata": { "id": "pfx-json", "version": "1.0" },
                "spec": {
                  "entry": "step",
                  "nodes": [
                    { "id": "step", "kind": "Agent", "ref": { "id": "agent-x", "version": "1.0" } },
                    { "id": "end",  "kind": "End" }
                  ],
                  "edges": [{ "from": "step", "to": "end", "when": "=Local.quality >= 0.8" }]
                }
              }
            ]
            """;

        var loader = new JsonAgentGraphManifestLoader();
        var graphs = await loader.LoadFromStringAsync(json);

        var edge = graphs[0].Edges.Single(e => e.From == "step" && e.To == "end");
        edge.When.Should().BeOfType<GraphEdgePredicate.Expression>()
            .Which.Expr.Should().Be("=Local.quality >= 0.8");
    }

    [Fact]
    public async Task When_InvalidString_Not_Always_And_Not_EqualSign_IsValidationError()
    {
        var yaml = """
            apiVersion: vais.agents/v1
            kind: AgentGraph
            metadata: { id: bad-graph, version: "1.0" }
            spec:
              entry: step
              nodes:
                - id: step
                  kind: Agent
                  ref: { id: agent-x }
                - id: end
                  kind: End
              edges:
                - from: step
                  to: end
                  when: "not-a-valid-predicate"
            """;

        var loader = new YamlAgentGraphManifestLoader();
        var act = async () => await loader.LoadFromStringAsync(yaml);
        await act.Should().ThrowAsync<AgentManifestValidationException>()
            .WithMessage("*'=' (PowerFx expression)*");
    }

    // ── §1d: node retry policy ───────────────────────────────────────────

    [Fact]
    public async Task RetryPolicy_Parses_From_Yaml()
    {
        var yaml = """
            apiVersion: vais.agents/v1
            kind: AgentGraph
            metadata: { id: retry-graph, version: "1.0" }
            spec:
              entry: step
              nodes:
                - id: step
                  kind: Agent
                  ref: { id: flaky-agent, version: "1.0" }
                  retryPolicy:
                    maxAttempts: 4
                    initialBackoffSeconds: 0.25
                    backoffMultiplier: 3
                    maxBackoffSeconds: 10
                - id: end
                  kind: End
              edges:
                - from: step
                  to: end
            """;

        var loader = new YamlAgentGraphManifestLoader();
        var graphs = await loader.LoadFromStringAsync(yaml);

        var policy = graphs[0].Nodes.Single(n => n.Id == "step").RetryPolicy;
        policy.Should().NotBeNull();
        policy!.MaxAttempts.Should().Be(4);
        policy.InitialBackoffSeconds.Should().Be(0.25);
        policy.BackoffMultiplier.Should().Be(3);
        policy.MaxBackoffSeconds.Should().Be(10);
    }

    [Fact]
    public async Task Envelope_RoundTrip_Preserves_RetryPolicy()
    {
        var original = new AgentGraphManifest(
            Id: "retry-round-trip", Version: "1.0", Entry: "step",
            Nodes: new[]
            {
                new GraphNode("step", "Agent",
                    Ref: new GraphAgentRef("flaky-agent", "1.0"),
                    RetryPolicy: new GraphNodeRetryPolicy(3, 0.5, 2.0, 30.0)),
                new GraphNode("end", "End"),
            },
            Edges: new[] { new GraphEdge("step", "end") });

        var envelope = AgentGraphManifestEnvelope.Serialize(original);
        var loader = new JsonAgentGraphManifestLoader();
        var roundTripped = await loader.LoadFromStringAsync(envelope);

        var policy = roundTripped[0].Nodes.Single(n => n.Id == "step").RetryPolicy;
        policy.Should().Be(new GraphNodeRetryPolicy(3, 0.5, 2.0, 30.0));
    }

    [Fact]
    public async Task RetryPolicy_InvalidMaxAttempts_Throws_Validation()
    {
        var yaml = """
            apiVersion: vais.agents/v1
            kind: AgentGraph
            metadata: { id: bad-retry, version: "1.0" }
            spec:
              entry: step
              nodes:
                - id: step
                  kind: Agent
                  ref: { id: a, version: "1.0" }
                  retryPolicy: { maxAttempts: 0 }
                - id: end
                  kind: End
              edges:
                - from: step
                  to: end
            """;

        var loader = new YamlAgentGraphManifestLoader();
        var act = async () => await loader.LoadFromStringAsync(yaml);
        await act.Should().ThrowAsync<AgentManifestValidationException>()
            .WithMessage("*maxAttempts*");
    }
}
