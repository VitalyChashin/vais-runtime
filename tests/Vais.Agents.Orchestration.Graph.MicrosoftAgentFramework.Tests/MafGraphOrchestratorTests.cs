// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using FluentAssertions;
using Microsoft.Agents.AI.Workflows;
using Vais.Agents.Control;
using Vais.Agents.Control.InProcess;
using Vais.Agents.Core;
using Vais.Agents.Hosting.InMemory;
using Xunit;

namespace Vais.Agents.Orchestration.Graph.MicrosoftAgentFramework.Tests;

/// <summary>
/// v0.9 PR 3: MAF Workflows adapter. Parity tests prove the MAF-backed and
/// in-process orchestrators produce the same output traces on deterministic
/// inputs. Additional coverage for MAF-specific paths (Workflow shape, halting
/// via RequestHalt, handlerRef dispatch).
/// </summary>
public sealed class MafGraphOrchestratorTests
{
    [Fact]
    public async Task Archetype_A_Pure_Handoff_Parity_With_InProcess()
    {
        var inprocEvents = await RunInProcess(BuildHandoffGraph(), SeedRefund());
        var mafEvents = await RunMaf(BuildHandoffGraph(), SeedRefund());

        EdgeSequence(mafEvents).Should().BeEquivalentTo(EdgeSequence(inprocEvents));
        mafEvents.OfType<GraphCompleted>().Should().ContainSingle();
        inprocEvents.OfType<GraphCompleted>().Should().ContainSingle();
    }

    [Fact]
    public async Task Archetype_B_Retrieval_Loop_Parity_With_InProcess()
    {
        // Provider is called globally across retrieve + answer + grade (3 calls per pass).
        // For 3 grade→retrieve loops we need grade calls 1-3 to be low-quality and grade
        // call 4 to be high. With the global counter that's "< 10 → 0.3; ≥ 10 → 0.9".
        int mafCalls = 0;
        var mafEvents = await RunMaf(
            BuildRetrievalLoop(),
            SeedRetrieval(),
            providerOverride: req =>
            {
                var callCount = System.Threading.Interlocked.Increment(ref mafCalls);
                return new CompletionResponse(callCount < 10 ? "{\"quality\":0.3}" : "{\"quality\":0.9}");
            });

        mafEvents.OfType<EdgeTraversed>().Count(e => e.From == "grade" && e.To == "retrieve")
            .Should().Be(3);
        mafEvents.OfType<EdgeTraversed>().Count(e => e.From == "grade" && e.To == "end")
            .Should().Be(1);
        mafEvents.OfType<GraphCompleted>().Should().ContainSingle();
    }

    [Fact]
    public async Task Cycle_Runs_Expected_Number_Of_Times()
    {
        // 5-iteration loop bounded by maxSteps=6 (5 hops + terminal).
        var manifest = new AgentGraphManifest(
            Id: "bounded-loop", Version: "1.0", Entry: "a",
            Nodes: new[]
            {
                new GraphNode("a", "Agent", Ref: new GraphAgentRef("counter")),
                new GraphNode("end", "End"),
            },
            Edges: new[]
            {
                new GraphEdge("a", "a",
                    When: new GraphEdgePredicate.PropertyMatcher("count", GraphPredicateOperator.Lt, JsonSerializer.SerializeToElement(5)),
                    OnTraverse: new GraphEdgeEffect.Increment("count")),
                new GraphEdge("a", "end"),
            })
        { MaxSteps = 100 };

        var (registry, lifecycle) = BuildHarness();
        await lifecycle.CreateAsync(ManifestFor("counter"));

        var orchestrator = new MafGraphOrchestrator(manifest, registry, lifecycle);
        var initial = new Dictionary<string, JsonElement> { ["count"] = JsonSerializer.SerializeToElement(0) };
        var final = await orchestrator.InvokeAsync(initial, new AgentContext());

        final["count"].GetInt32().Should().Be(5);
    }

    [Fact]
    public async Task Interrupt_Node_Emits_Event_And_Halts()
    {
        var (registry, lifecycle) = BuildHarness();
        await lifecycle.CreateAsync(ManifestFor("pre"));

        var manifest = new AgentGraphManifest(
            Id: "interruptible", Version: "1.0", Entry: "pre",
            Nodes: new[]
            {
                new GraphNode("pre", "Agent", Ref: new GraphAgentRef("pre")),
                new GraphNode("wait", "Interrupt", InterruptReason: "awaiting human"),
                new GraphNode("end", "End"),
            },
            Edges: new[]
            {
                new GraphEdge("pre", "wait"),
                new GraphEdge("wait", "end"),
            });

        var orchestrator = new MafGraphOrchestrator(manifest, registry, lifecycle);
        var events = new List<AgentGraphEvent>();
        await foreach (var e in orchestrator.StreamAsync(new Dictionary<string, JsonElement>(), new AgentContext()))
        {
            events.Add(e);
        }

        events.OfType<GraphInterrupted>().Should().ContainSingle()
            .Which.Reason.Should().Be("awaiting human");
        // Halt prevents reaching the End node, so no GraphCompleted is emitted.
        events.OfType<GraphCompleted>().Should().BeEmpty();
    }

    [Fact]
    public async Task OutputSchema_Binding_Extracts_Structured_Response()
    {
        var (registry, lifecycle) = BuildHarness(_ => new CompletionResponse("{\"quality\":0.42,\"note\":\"meh\"}"));
        await lifecycle.CreateAsync(ManifestFor("grader"));

        var manifest = new AgentGraphManifest(
            Id: "grade-only", Version: "1.0", Entry: "g",
            Nodes: new[]
            {
                new GraphNode("g", "Agent", Ref: new GraphAgentRef("grader"),
                    StateBindings: new GraphStateBindings(Output: new[] { "quality" })),
                new GraphNode("end", "End"),
            },
            Edges: new[] { new GraphEdge("g", "end") });

        var orchestrator = new MafGraphOrchestrator(manifest, registry, lifecycle);
        var final = await orchestrator.InvokeAsync(new Dictionary<string, JsonElement>(), new AgentContext());

        final.Should().ContainKey("quality");
        final["quality"].GetDouble().Should().BeApproximately(0.42, 1e-6);
        final.Should().NotContainKey("note");
    }

    [Fact]
    public async Task HandlerRef_Edge_Predicate_Resolves_From_DI()
    {
        var (registry, lifecycle) = BuildHarness();
        await lifecycle.CreateAsync(ManifestFor("a"));
        await lifecycle.CreateAsync(ManifestFor("b"));

        var manifest = new AgentGraphManifest(
            Id: "custom-pred", Version: "1.0", Entry: "a",
            Nodes: new[]
            {
                new GraphNode("a", "Agent", Ref: new GraphAgentRef("a")),
                new GraphNode("b", "Agent", Ref: new GraphAgentRef("b")),
                new GraphNode("end", "End"),
            },
            Edges: new[]
            {
                new GraphEdge("a", "b",
                    When: new GraphEdgePredicate.HandlerRef(new GraphHandlerRef("CustomPred"))),
                new GraphEdge("a", "end"),
                new GraphEdge("b", "end"),
            });

        IGraphEdgePredicate resolver(GraphHandlerRef r) => new AlwaysTruePredicate();
        var orchestrator = new MafGraphOrchestrator(manifest, registry, lifecycle, predicateResolver: resolver);

        var events = new List<AgentGraphEvent>();
        await foreach (var e in orchestrator.StreamAsync(new Dictionary<string, JsonElement>(), new AgentContext()))
        {
            events.Add(e);
        }

        events.OfType<EdgeTraversed>().Should().Contain(e => e.From == "a" && e.To == "b");
    }

    [Fact]
    public async Task HandlerRef_Node_Runs_Code_Backed_Executor()
    {
        var (registry, lifecycle) = BuildHarness();

        var manifest = new AgentGraphManifest(
            Id: "code-node", Version: "1.0", Entry: "calc",
            Nodes: new[]
            {
                new GraphNode("calc", "Code", HandlerRef: new GraphHandlerRef("DoubleIt"),
                    StateBindings: new GraphStateBindings(Input: new[] { "n" }, Output: new[] { "n" })),
                new GraphNode("end", "End"),
            },
            Edges: new[] { new GraphEdge("calc", "end") });

        IGraphCodeNode resolver(GraphHandlerRef r) => new DoublingCodeNode();
        var orchestrator = new MafGraphOrchestrator(manifest, registry, lifecycle, codeNodeResolver: resolver);

        var initial = new Dictionary<string, JsonElement> { ["n"] = JsonSerializer.SerializeToElement(21) };
        var final = await orchestrator.InvokeAsync(initial, new AgentContext());

        final["n"].GetInt32().Should().Be(42);
    }

    [Fact]
    public async Task MaxSteps_Ceiling_Throws_GraphRecursionException()
    {
        var (registry, lifecycle) = BuildHarness();
        await lifecycle.CreateAsync(ManifestFor("forever"));

        var manifest = new AgentGraphManifest(
            Id: "infinite-maf", Version: "1.0", Entry: "a",
            Nodes: new[]
            {
                new GraphNode("a", "Agent", Ref: new GraphAgentRef("forever")),
                new GraphNode("b", "Agent", Ref: new GraphAgentRef("forever")),
            },
            Edges: new[]
            {
                new GraphEdge("a", "b"),
                new GraphEdge("b", "a"),
            })
        { MaxSteps = 4 };

        var orchestrator = new MafGraphOrchestrator(manifest, registry, lifecycle);
        await FluentActions.Invoking(async () => await orchestrator.InvokeAsync(
                new Dictionary<string, JsonElement>(), new AgentContext()))
            .Should().ThrowAsync<GraphRecursionException>();
    }

    [Fact]
    public void MafGraphBuilder_Preserves_Node_Ids_On_Built_Workflow()
    {
        var (registry, lifecycle) = BuildHarness();
        var manifest = BuildHandoffGraph();

        var workflow = MafGraphBuilder.Build(manifest, registry, lifecycle);

        workflow.StartExecutorId.Should().Be("triage");
        workflow.Name.Should().Be("customer-router");
    }

    // ---- helpers ----

    private static AgentGraphManifest BuildHandoffGraph() => new(
        Id: "customer-router", Version: "1.0", Entry: "triage",
        Nodes: new[]
        {
            new GraphNode("triage", "Agent", Ref: new GraphAgentRef("triage-agent")),
            new GraphNode("billing", "Agent", Ref: new GraphAgentRef("billing-agent")),
            new GraphNode("sales", "Agent", Ref: new GraphAgentRef("sales-agent")),
            new GraphNode("end", "End"),
        },
        Edges: new[]
        {
            new GraphEdge("triage", "billing",
                When: new GraphEdgePredicate.PropertyMatcher("lastMessage.Text",
                    GraphPredicateOperator.Contains, JsonSerializer.SerializeToElement("refund"))),
            new GraphEdge("triage", "sales",
                When: new GraphEdgePredicate.PropertyMatcher("lastMessage.Text",
                    GraphPredicateOperator.Contains, JsonSerializer.SerializeToElement("upgrade"))),
            new GraphEdge("triage", "end"),
            new GraphEdge("billing", "end"),
            new GraphEdge("sales", "end"),
        });

    private static AgentGraphManifest BuildRetrievalLoop() => new(
        Id: "reflective-qa", Version: "1.0", Entry: "retrieve",
        Nodes: new[]
        {
            new GraphNode("retrieve", "Agent", Ref: new GraphAgentRef("retrieval-agent")),
            new GraphNode("answer", "Agent", Ref: new GraphAgentRef("answering-agent")),
            new GraphNode("grade", "Agent", Ref: new GraphAgentRef("grading-agent"),
                StateBindings: new GraphStateBindings(Output: new[] { "quality" })),
            new GraphNode("end", "End"),
        },
        Edges: new[]
        {
            new GraphEdge("retrieve", "answer"),
            new GraphEdge("answer", "grade"),
            new GraphEdge("grade", "retrieve",
                When: new GraphEdgePredicate.AllOf(new[]
                {
                    (GraphEdgePredicate)new GraphEdgePredicate.PropertyMatcher("quality", GraphPredicateOperator.Lt, JsonSerializer.SerializeToElement(0.7)),
                    new GraphEdgePredicate.PropertyMatcher("retryCount", GraphPredicateOperator.Lt, JsonSerializer.SerializeToElement(3)),
                }),
                OnTraverse: new GraphEdgeEffect.Increment("retryCount")),
            new GraphEdge("grade", "end"),
        })
    { MaxSteps = 50 };

    private static Dictionary<string, JsonElement> SeedRefund() => new()
    {
        [GraphStateReducers.WellKnownKey.Messages] = JsonSerializer.SerializeToElement(new[]
        {
            JsonSerializer.SerializeToElement(new ChatTurn(AgentChatRole.User, "I need a refund for my last order")),
        }),
    };

    private static Dictionary<string, JsonElement> SeedRetrieval() => new()
    {
        ["query"] = JsonSerializer.SerializeToElement("what is the best plan?"),
        ["retryCount"] = JsonSerializer.SerializeToElement(0),
    };

    private static async Task<List<AgentGraphEvent>> RunInProcess(
        AgentGraphManifest manifest,
        Dictionary<string, JsonElement> initial,
        Func<CompletionRequest, CompletionResponse>? providerOverride = null)
    {
        var (registry, lifecycle) = BuildHarness(providerOverride);
        foreach (var n in manifest.Nodes.Where(n => n.Kind == "Agent" && n.Ref is not null))
        {
            await lifecycle.CreateAsync(ManifestFor(n.Ref!.Id));
        }
        var orchestrator = new InProcessGraphOrchestrator(manifest, registry, lifecycle, new InMemoryCheckpointer());
        var events = new List<AgentGraphEvent>();
        await foreach (var e in orchestrator.StreamAsync(initial, new AgentContext())) events.Add(e);
        return events;
    }

    private static async Task<List<AgentGraphEvent>> RunMaf(
        AgentGraphManifest manifest,
        Dictionary<string, JsonElement> initial,
        Func<CompletionRequest, CompletionResponse>? providerOverride = null)
    {
        var (registry, lifecycle) = BuildHarness(providerOverride);
        foreach (var n in manifest.Nodes.Where(n => n.Kind == "Agent" && n.Ref is not null))
        {
            await lifecycle.CreateAsync(ManifestFor(n.Ref!.Id));
        }
        var orchestrator = new MafGraphOrchestrator(manifest, registry, lifecycle);
        var events = new List<AgentGraphEvent>();
        await foreach (var e in orchestrator.StreamAsync(initial, new AgentContext())) events.Add(e);
        return events;
    }

    private static List<(string From, string To)> EdgeSequence(List<AgentGraphEvent> events)
        => events.OfType<EdgeTraversed>().Select(e => (e.From, e.To)).ToList();

    private static (InMemoryAgentRegistry registry, AgentLifecycleManager lifecycle) BuildHarness(
        Func<CompletionRequest, CompletionResponse>? provider = null)
    {
        var registry = new InMemoryAgentRegistry();
        var runtime = new InMemoryAgentRuntime(new FakeCompletionProvider(
            provider ?? (req => new CompletionResponse($"echoed: {req.History.LastOrDefault()?.Text}"))));
        var lifecycle = new AgentLifecycleManager(registry, runtime);
        return (registry, lifecycle);
    }

    private static AgentManifest ManifestFor(string id) => new(
        Id: id, Version: "1.0",
        Handler: new AgentHandlerRef("declarative"),
        Protocols: new[] { new ProtocolBinding("Http") },
        Tools: Array.Empty<ToolRef>());

    private sealed class AlwaysTruePredicate : IGraphEdgePredicate
    {
        public ValueTask<bool> EvaluateAsync(IReadOnlyDictionary<string, JsonElement> state, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(true);
    }

    private sealed class DoublingCodeNode : IGraphCodeNode
    {
        public ValueTask<IReadOnlyDictionary<string, JsonElement>> ExecuteAsync(
            IReadOnlyDictionary<string, JsonElement> input, AgentContext context, CancellationToken cancellationToken = default)
        {
            var n = input["n"].GetInt32();
            IReadOnlyDictionary<string, JsonElement> result = new Dictionary<string, JsonElement>
            {
                ["n"] = JsonSerializer.SerializeToElement(n * 2),
            };
            return ValueTask.FromResult(result);
        }
    }

    private sealed class FakeCompletionProvider : ICompletionProvider
    {
        private readonly Func<CompletionRequest, CompletionResponse> _respond;
        public FakeCompletionProvider(Func<CompletionRequest, CompletionResponse> respond) => _respond = respond;
        public string ProviderName => "Fake";
        public Task<CompletionResponse> CompleteAsync(CompletionRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(_respond(request));
    }
}
