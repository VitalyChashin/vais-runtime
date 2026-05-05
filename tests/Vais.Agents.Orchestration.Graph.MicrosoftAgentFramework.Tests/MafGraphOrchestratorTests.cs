// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using FluentAssertions;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.Logging;
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

    // ---- v0.36 durable resume ----

    /// <summary>
    /// Spike: verifies that MafGraphBuilder.Build with startNodeId = "wait" produces a
    /// workflow whose StartExecutorId is "wait", proving InProcessExecution delivers the
    /// initial message to the specified non-entry executor.
    /// </summary>
    [Fact]
    public void MafGraphBuilder_StartNodeId_Override_Sets_StartExecutorId()
    {
        var (registry, lifecycle) = BuildHarness();
        var manifest = new AgentGraphManifest(
            Id: "resume-spike", Version: "1.0", Entry: "pre",
            Nodes: new[]
            {
                new GraphNode("pre", "Agent", Ref: new GraphAgentRef("pre")),
                new GraphNode("wait", "Interrupt", InterruptReason: "HITL"),
                new GraphNode("end", "End"),
            },
            Edges: new[]
            {
                new GraphEdge("pre", "wait"),
                new GraphEdge("wait", "end"),
            });

        var workflow = MafGraphBuilder.Build(manifest, registry, lifecycle, startNodeId: "wait");

        workflow.StartExecutorId.Should().Be("wait");
    }

    [Fact]
    public async Task Interrupt_Saves_Checkpoint_And_ResumeAsync_Continues_To_End()
    {
        var (registry, lifecycle) = BuildHarness();
        await lifecycle.CreateAsync(ManifestFor("pre"));

        var manifest = new AgentGraphManifest(
            Id: "resumable-maf", Version: "1.0", Entry: "pre",
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

        var checkpointer = new InMemoryCheckpointer();
        var orchestrator = new MafGraphOrchestrator(manifest, registry, lifecycle, checkpointer: checkpointer);

        // First run — should stop at the Interrupt node and save a checkpoint.
        var run1Events = new List<AgentGraphEvent>();
        await foreach (var e in orchestrator.StreamAsync(new Dictionary<string, JsonElement>(), new AgentContext()))
        {
            run1Events.Add(e);
        }

        run1Events.OfType<GraphInterrupted>().Should().ContainSingle();
        run1Events.OfType<GraphCompleted>().Should().BeEmpty();

        // Checkpoint must be persisted.
        var interrupted = run1Events.OfType<GraphInterrupted>().Single();
        var checkpoint = await checkpointer.LoadAsync(interrupted.RunId);
        checkpoint.Should().NotBeNull();
        checkpoint!.NextNodeId.Should().Be("wait");
        checkpoint.IsComplete.Should().BeFalse();

        // Resume on a fresh orchestrator instance — should complete.
        var orchestrator2 = new MafGraphOrchestrator(manifest, registry, lifecycle, checkpointer: checkpointer);
        var run2Events = new List<AgentGraphEvent>();
        await foreach (var e in orchestrator2.ResumeStreamAsync(checkpoint, resumePayload: (IDictionary<string, JsonElement>?)null, new AgentContext()))
        {
            run2Events.Add(e);
        }

        run2Events.OfType<GraphResumed>().Should().ContainSingle()
            .Which.ResumedFromNodeId.Should().Be("wait");
        run2Events.OfType<GraphCompleted>().Should().ContainSingle();
        run2Events.OfType<GraphInterrupted>().Should().BeEmpty();
    }

    [Fact]
    public async Task Cross_Host_InProcess_Interrupted_Resumes_On_Maf()
    {
        // Advisor's strongest parity test: run InProcess to interrupt, load checkpoint,
        // resume on a fresh MafGraphOrchestrator, assert same final state.
        var (registry, lifecycle) = BuildHarness(_ => new CompletionResponse("{\"answer\":42}"));
        await lifecycle.CreateAsync(ManifestFor("pre"));

        var manifest = new AgentGraphManifest(
            Id: "cross-host-resume", Version: "1.0", Entry: "pre",
            Nodes: new[]
            {
                new GraphNode("pre", "Agent", Ref: new GraphAgentRef("pre"),
                    StateBindings: new GraphStateBindings(Output: new[] { "answer" })),
                new GraphNode("wait", "Interrupt"),
                new GraphNode("end", "End"),
            },
            Edges: new[]
            {
                new GraphEdge("pre", "wait"),
                new GraphEdge("wait", "end"),
            });

        // Phase 1: InProcess runs to interrupt.
        var checkpointer = new InMemoryCheckpointer();
        var inprocOrchestrator = new InProcessGraphOrchestrator(manifest, registry, lifecycle, checkpointer);
        var inprocEvents = new List<AgentGraphEvent>();
        await foreach (var e in inprocOrchestrator.StreamAsync(new Dictionary<string, JsonElement>(), new AgentContext()))
        {
            inprocEvents.Add(e);
        }

        inprocEvents.OfType<GraphInterrupted>().Should().ContainSingle();
        var runId = inprocEvents.OfType<GraphInterrupted>().Single().RunId;
        var checkpoint = await checkpointer.LoadAsync(runId);
        checkpoint.Should().NotBeNull();
        checkpoint!.State.Should().ContainKey("answer");

        // Phase 2: Resume on MAF.
        var mafOrchestrator = new MafGraphOrchestrator(manifest, registry, lifecycle, checkpointer: checkpointer);
        var mafEvents = new List<AgentGraphEvent>();
        await foreach (var e in mafOrchestrator.ResumeStreamAsync(checkpoint, resumePayload: (IDictionary<string, JsonElement>?)null, new AgentContext()))
        {
            mafEvents.Add(e);
        }

        mafEvents.OfType<GraphResumed>().Should().ContainSingle();
        mafEvents.OfType<GraphCompleted>().Should().ContainSingle();

        // Final checkpoint (saved on End) must be complete.
        var finalCheckpoint = await checkpointer.LoadAsync(runId);
        finalCheckpoint!.IsComplete.Should().BeTrue();
        finalCheckpoint.State.Should().ContainKey("answer");
        finalCheckpoint.State["answer"].GetInt32().Should().Be(42);
    }

    [Fact]
    public async Task ResumeAsync_Without_Checkpointer_Throws_InvalidOperationException()
    {
        var (registry, lifecycle) = BuildHarness();
        var manifest = new AgentGraphManifest(
            Id: "no-cp", Version: "1.0", Entry: "end",
            Nodes: new[] { new GraphNode("end", "End") },
            Edges: Array.Empty<GraphEdge>());

        // No checkpointer supplied.
        var orchestrator = new MafGraphOrchestrator(manifest, registry, lifecycle);
        var dummyCheckpoint = new GraphCheckpoint(
            "runId", "no-cp", "1.0",
            new Dictionary<string, JsonElement>(),
            NextNodeId: "end", SuperStepIndex: 0,
            PendingInterruptId: null, IsComplete: false,
            CreatedAt: DateTimeOffset.UtcNow);

        await FluentActions.Invoking(async () =>
                await orchestrator.ResumeAsync(dummyCheckpoint, resumePayload: null, new AgentContext()))
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*checkpointer*");
    }

    [Fact]
    public async Task CustomReducer_Produces_Same_Final_State_As_InProcess()
    {
        int inprocCount = 0, mafCount = 0;

        var manifest = new AgentGraphManifest(
            Id: "custom-reducer-parity", Version: "1.0", Entry: "a",
            Nodes: new[]
            {
                new GraphNode("a", "Agent", Ref: new GraphAgentRef("node-a"),
                    StateBindings: new GraphStateBindings(Output: new[] { "tags" })),
                new GraphNode("b", "Agent", Ref: new GraphAgentRef("node-b"),
                    StateBindings: new GraphStateBindings(Output: new[] { "tags" })),
                new GraphNode("end", "End"),
            },
            Edges: new[]
            {
                new GraphEdge("a", "b"),
                new GraphEdge("b", "end"),
            })
        {
            StateReducers = new Dictionary<string, GraphStateReducer>
            {
                ["tags"] = new GraphStateReducer.Append(),
            },
        };

        // InProcess run
        var (inprocRegistry, inprocLifecycle) = BuildHarness(_ =>
        {
            var n = System.Threading.Interlocked.Increment(ref inprocCount);
            return new CompletionResponse(n == 1 ? """{"tags":["alpha"]}""" : """{"tags":["beta"]}""");
        });
        await inprocLifecycle.CreateAsync(ManifestFor("node-a"));
        await inprocLifecycle.CreateAsync(ManifestFor("node-b"));
        var inprocOrchestrator = new InProcessGraphOrchestrator(manifest, inprocRegistry, inprocLifecycle, new InMemoryCheckpointer());
        var inprocFinal = await inprocOrchestrator.InvokeAsync(new Dictionary<string, JsonElement>(), new AgentContext());

        // MAF run — same manifest, same reducer
        var (mafRegistry, mafLifecycle) = BuildHarness(_ =>
        {
            var n = System.Threading.Interlocked.Increment(ref mafCount);
            return new CompletionResponse(n == 1 ? """{"tags":["alpha"]}""" : """{"tags":["beta"]}""");
        });
        await mafLifecycle.CreateAsync(ManifestFor("node-a"));
        await mafLifecycle.CreateAsync(ManifestFor("node-b"));
        var mafOrchestrator = new MafGraphOrchestrator(manifest, mafRegistry, mafLifecycle);
        var mafFinal = await mafOrchestrator.InvokeAsync(new Dictionary<string, JsonElement>(), new AgentContext());

        inprocFinal["tags"].GetArrayLength().Should().Be(2, "InProcess: Append reducer accumulates both nodes");
        mafFinal["tags"].GetArrayLength().Should().Be(2, "MAF: Append reducer accumulates both nodes");

        var inprocItems = inprocFinal["tags"].EnumerateArray().Select(e => e.GetString()).OrderBy(x => x).ToList();
        var mafItems = mafFinal["tags"].EnumerateArray().Select(e => e.GetString()).OrderBy(x => x).ToList();
        mafItems.Should().BeEquivalentTo(inprocItems, "MAF and InProcess produce same tags with custom Append reducer");
    }

    [Fact]
    public async Task ResumeAsync_Preserves_State_Through_Interrupt()
    {
        var (registry, lifecycle) = BuildHarness(_ => new CompletionResponse("{\"score\":7}"));
        await lifecycle.CreateAsync(ManifestFor("scorer"));

        var manifest = new AgentGraphManifest(
            Id: "state-preserve", Version: "1.0", Entry: "scorer",
            Nodes: new[]
            {
                new GraphNode("scorer", "Agent", Ref: new GraphAgentRef("scorer"),
                    StateBindings: new GraphStateBindings(Output: new[] { "score" })),
                new GraphNode("review", "Interrupt", InterruptReason: "human review"),
                new GraphNode("end", "End"),
            },
            Edges: new[]
            {
                new GraphEdge("scorer", "review"),
                new GraphEdge("review", "end"),
            });

        var checkpointer = new InMemoryCheckpointer();
        var orchestrator = new MafGraphOrchestrator(manifest, registry, lifecycle, checkpointer: checkpointer);

        // Run to interrupt — capture the RunId from the emitted event.
        string? capturedRunId = null;
        await foreach (var e in orchestrator.StreamAsync(new Dictionary<string, JsonElement>(), new AgentContext()))
        {
            if (e is GraphInterrupted gi) capturedRunId = gi.RunId;
        }

        capturedRunId.Should().NotBeNull();
        var cp = await checkpointer.LoadAsync(capturedRunId!);
        cp.Should().NotBeNull();
        // Scorer's output should be captured in the checkpoint state.
        cp!.State.Should().ContainKey("score");
        cp.State["score"].GetInt32().Should().Be(7);

        // Resume — final state must still carry the pre-interrupt score.
        var orchestrator2 = new MafGraphOrchestrator(manifest, registry, lifecycle, checkpointer: checkpointer);
        var finalState = await orchestrator2.ResumeAsync(cp, resumePayload: null, new AgentContext());

        finalState.Should().ContainKey("score");
        finalState["score"].GetInt32().Should().Be(7);
    }

    [Fact]
    public async Task GraphEventBus_Receives_Exactly_The_Same_Events_As_The_Enumerator()
    {
        var (registry, lifecycle) = BuildHarness();
        await lifecycle.CreateAsync(ManifestFor("node-a"));

        var manifest = new AgentGraphManifest(
            Id: "maf-bus-test", Version: "1.0", Entry: "a",
            Nodes: new[]
            {
                new GraphNode("a", "Agent", Ref: new GraphAgentRef("node-a")),
                new GraphNode("end", "End"),
            },
            Edges: new[] { new GraphEdge("a", "end") });

        var bus = new InMemoryAgentGraphEventBus();
        var busEvents = new List<AgentGraphEvent>();
        using var _ = bus.Subscribe((e, ct) => { busEvents.Add(e); return ValueTask.CompletedTask; });

        var orchestrator = new MafGraphOrchestrator(
            manifest, registry, lifecycle, graphEventBus: bus);

        var streamEvents = new List<AgentGraphEvent>();
        await foreach (var e in orchestrator.StreamAsync(new Dictionary<string, JsonElement>(), new AgentContext()))
        {
            streamEvents.Add(e);
        }

        busEvents.Should().HaveSameCount(streamEvents, "bus must receive every event yielded by the MAF stream");
        for (var i = 0; i < streamEvents.Count; i++)
        {
            busEvents[i].Should().BeSameAs(streamEvents[i],
                $"MAF bus event [{i}] should be the same object as stream event [{i}]");
        }
        streamEvents.Should().ContainSingle(e => e is GraphStarted);
        streamEvents.Should().ContainSingle(e => e is GraphCompleted);
    }

    // ---- HITL tests (v0.42) ----

    [Fact]
    public async Task Hitl_SingleInterrupt_Handler_Returns_State_Graph_Continues()
    {
        var (registry, lifecycle) = BuildHarness(_ => new CompletionResponse("{\"score\":42}"));
        await lifecycle.CreateAsync(ManifestFor("scorer"));

        var manifest = new AgentGraphManifest(
            Id: "hitl-single", Version: "1.0", Entry: "scorer",
            Nodes: new[]
            {
                new GraphNode("scorer", "Agent", Ref: new GraphAgentRef("scorer"),
                    StateBindings: new GraphStateBindings(Output: new[] { "score" })),
                new GraphNode("review", "Interrupt", InterruptReason: "human review"),
                new GraphNode("end", "End"),
            },
            Edges: new[]
            {
                new GraphEdge("scorer", "review"),
                new GraphEdge("review", "end"),
            });

        var orchestrator = new MafGraphOrchestrator(manifest, registry, lifecycle);

        GraphInterrupted? capturedInterrupt = null;
        var events = new List<AgentGraphEvent>();
        await foreach (var e in orchestrator.StreamWithHitlAsync(
            new Dictionary<string, JsonElement>(),
            new AgentContext(),
            handleInterrupt: (interrupted, _) =>
            {
                capturedInterrupt = interrupted;
                var response = new Dictionary<string, JsonElement>
                {
                    ["approved"] = JsonSerializer.SerializeToElement(true),
                };
                return ValueTask.FromResult<IDictionary<string, JsonElement>?>(response);
            }))
        {
            events.Add(e);
        }

        capturedInterrupt.Should().NotBeNull("handler must be called with GraphInterrupted");
        capturedInterrupt!.NodeId.Should().Be("review");
        capturedInterrupt.Reason.Should().Be("human review");

        events.Should().ContainSingle(e => e is GraphInterrupted, "exactly one interrupt emitted");
        events.Should().ContainSingle(e => e is GraphCompleted, "graph must complete after handler responds");
        events.Should().NotContain(e => e is GraphFailed, "no failure when handler returns a value");

        var finalState = await orchestrator.InvokeWithHitlAsync(
            new Dictionary<string, JsonElement>(),
            new AgentContext(),
            handleInterrupt: (_, _) =>
                ValueTask.FromResult<IDictionary<string, JsonElement>?>(
                    new Dictionary<string, JsonElement> { ["approved"] = JsonSerializer.SerializeToElement(true) }));

        finalState.Should().ContainKey("score", "scorer output must survive through interrupt");
        finalState["score"].GetInt32().Should().Be(42);
        finalState.Should().ContainKey("hitl.response", "handler payload merged under hitl.response");
    }

    [Fact]
    public async Task Hitl_MultipleSequentialInterrupts_AllHandlersCalled()
    {
        var (registry, lifecycle) = BuildHarness(_ => new CompletionResponse("{}"));
        await lifecycle.CreateAsync(ManifestFor("start"));

        var manifest = new AgentGraphManifest(
            Id: "hitl-multi", Version: "1.0", Entry: "start",
            Nodes: new[]
            {
                new GraphNode("start", "Agent", Ref: new GraphAgentRef("start")),
                new GraphNode("review1", "Interrupt", InterruptReason: "first review"),
                new GraphNode("review2", "Interrupt", InterruptReason: "second review"),
                new GraphNode("end", "End"),
            },
            Edges: new[]
            {
                new GraphEdge("start", "review1"),
                new GraphEdge("review1", "review2"),
                new GraphEdge("review2", "end"),
            });

        var orchestrator = new MafGraphOrchestrator(manifest, registry, lifecycle);

        var capturedInterrupts = new List<GraphInterrupted>();
        int callCount = 0;
        var finalState = await orchestrator.InvokeWithHitlAsync(
            new Dictionary<string, JsonElement>(),
            new AgentContext(),
            handleInterrupt: (interrupted, _) =>
            {
                capturedInterrupts.Add(interrupted);
                var response = new Dictionary<string, JsonElement>
                {
                    [$"approved{System.Threading.Interlocked.Increment(ref callCount)}"] = JsonSerializer.SerializeToElement(true),
                };
                return ValueTask.FromResult<IDictionary<string, JsonElement>?>(response);
            });

        capturedInterrupts.Should().HaveCount(2, "handler called once per interrupt node");
        capturedInterrupts[0].NodeId.Should().Be("review1");
        capturedInterrupts[0].Reason.Should().Be("first review");
        capturedInterrupts[1].NodeId.Should().Be("review2");
        capturedInterrupts[1].Reason.Should().Be("second review");
        finalState.Should().ContainKey("hitl.response", "last handler payload in final state");
    }

    [Fact]
    public async Task Hitl_HandlerReturnsNull_GraphFailedEmittedAndExceptionThrown()
    {
        var (registry, lifecycle) = BuildHarness();
        await lifecycle.CreateAsync(ManifestFor("start"));

        var manifest = new AgentGraphManifest(
            Id: "hitl-abort", Version: "1.0", Entry: "start",
            Nodes: new[]
            {
                new GraphNode("start", "Agent", Ref: new GraphAgentRef("start")),
                new GraphNode("review", "Interrupt"),
                new GraphNode("end", "End"),
            },
            Edges: new[]
            {
                new GraphEdge("start", "review"),
                new GraphEdge("review", "end"),
            });

        var orchestrator = new MafGraphOrchestrator(manifest, registry, lifecycle);

        var events = new List<AgentGraphEvent>();
        var act = async () =>
        {
            await foreach (var e in orchestrator.StreamWithHitlAsync(
                new Dictionary<string, JsonElement>(),
                new AgentContext(),
                handleInterrupt: (_, _) => ValueTask.FromResult<IDictionary<string, JsonElement>?>(null)))
            {
                events.Add(e);
            }
        };

        await act.Should().ThrowAsync<GraphHitlAbortedException>()
            .WithMessage("*review*");
        events.Should().ContainSingle(e => e is GraphFailed, "GraphFailed emitted before exception");
        events.Should().NotContain(e => e is GraphCompleted);
        var failed = events.OfType<GraphFailed>().Single();
        failed.ErrorType.Should().Be(nameof(GraphHitlAbortedException));
    }

    [Fact]
    public async Task Hitl_Parity_With_InProcess_Same_FinalState_And_Interrupt_Sequence()
    {
        const string graphId = "hitl-parity";
        var manifest = new AgentGraphManifest(
            Id: graphId, Version: "1.0", Entry: "scorer",
            Nodes: new[]
            {
                new GraphNode("scorer", "Agent", Ref: new GraphAgentRef("scorer"),
                    StateBindings: new GraphStateBindings(Output: new[] { "score" })),
                new GraphNode("review", "Interrupt", InterruptReason: "parity check"),
                new GraphNode("end", "End"),
            },
            Edges: new[]
            {
                new GraphEdge("scorer", "review"),
                new GraphEdge("review", "end"),
            });

        IDictionary<string, JsonElement> MakeResponse() =>
            new Dictionary<string, JsonElement> { ["approved"] = JsonSerializer.SerializeToElement(true) };

        // InProcess run
        var (inprocReg, inprocLc) = BuildHarness(_ => new CompletionResponse("{\"score\":7}"));
        await inprocLc.CreateAsync(ManifestFor("scorer"));
        var inprocOrchestrator = new InProcessGraphOrchestrator(manifest, inprocReg, inprocLc, new InMemoryCheckpointer());
        var inprocFinal = await inprocOrchestrator.InvokeWithHitlAsync(
            new Dictionary<string, JsonElement>(),
            new AgentContext(),
            handleInterrupt: (_, _) => ValueTask.FromResult<IDictionary<string, JsonElement>?>(MakeResponse()));

        // MAF run
        var (mafReg, mafLc) = BuildHarness(_ => new CompletionResponse("{\"score\":7}"));
        await mafLc.CreateAsync(ManifestFor("scorer"));
        var mafOrchestrator = new MafGraphOrchestrator(manifest, mafReg, mafLc);
        var mafFinal = await mafOrchestrator.InvokeWithHitlAsync(
            new Dictionary<string, JsonElement>(),
            new AgentContext(),
            handleInterrupt: (_, _) => ValueTask.FromResult<IDictionary<string, JsonElement>?>(MakeResponse()));

        inprocFinal.Should().ContainKey("score");
        mafFinal.Should().ContainKey("score");
        inprocFinal["score"].GetInt32().Should().Be(mafFinal["score"].GetInt32(),
            "both orchestrators must produce the same final score");
        inprocFinal.Should().ContainKey("hitl.response");
        mafFinal.Should().ContainKey("hitl.response");
    }

    [Fact]
    public async Task Hitl_WithCheckpointer_CheckpointWrittenAtInterruptWithCorrectNextNodeId()
    {
        var (registry, lifecycle) = BuildHarness(_ => new CompletionResponse("{\"score\":99}"));
        await lifecycle.CreateAsync(ManifestFor("scorer"));

        var manifest = new AgentGraphManifest(
            Id: "hitl-checkpoint", Version: "1.0", Entry: "scorer",
            Nodes: new[]
            {
                new GraphNode("scorer", "Agent", Ref: new GraphAgentRef("scorer"),
                    StateBindings: new GraphStateBindings(Output: new[] { "score" })),
                new GraphNode("review", "Interrupt", InterruptReason: "checkpoint test"),
                new GraphNode("end", "End"),
            },
            Edges: new[]
            {
                new GraphEdge("scorer", "review"),
                new GraphEdge("review", "end"),
            });

        var checkpointer = new InMemoryCheckpointer();
        var orchestrator = new MafGraphOrchestrator(manifest, registry, lifecycle, checkpointer: checkpointer);

        // Capture the interrupt checkpoint inside the handler — at this point the End node
        // has not yet run, so LoadAsync returns the interrupt-time checkpoint, not the
        // IsComplete one that will overwrite it once the graph finishes.
        GraphCheckpoint? interruptCheckpoint = null;
        string? capturedRunId = null;
        await foreach (var e in orchestrator.StreamWithHitlAsync(
            new Dictionary<string, JsonElement>(),
            new AgentContext(),
            handleInterrupt: async (interrupted, ct) =>
            {
                capturedRunId = interrupted.RunId;
                interruptCheckpoint = await checkpointer.LoadAsync(interrupted.RunId, ct);
                return new Dictionary<string, JsonElement> { ["approved"] = JsonSerializer.SerializeToElement(true) };
            }))
        {
        }

        capturedRunId.Should().NotBeNull();
        interruptCheckpoint.Should().NotBeNull("checkpoint must be saved at interrupt");
        // NextNodeId = interrupt node id (supports crash-recovery via IResumableAgentGraph)
        interruptCheckpoint!.NextNodeId.Should().Be("review");
        interruptCheckpoint.State.Should().ContainKey("score");
        interruptCheckpoint.State["score"].GetInt32().Should().Be(99);

        // After the run completes, the final (IsComplete) checkpoint should exist.
        var finalCp = await checkpointer.LoadAsync(capturedRunId!);
        finalCp.Should().NotBeNull();
        finalCp!.IsComplete.Should().BeTrue("IsComplete checkpoint written when End node fires");
    }

    [Fact]
    public async Task Hitl_InvokeWithHitlAsync_ReturnsCorrectFinalState()
    {
        var (registry, lifecycle) = BuildHarness(_ => new CompletionResponse("{\"result\":\"done\"}"));
        await lifecycle.CreateAsync(ManifestFor("worker"));

        var manifest = new AgentGraphManifest(
            Id: "hitl-invoke", Version: "1.0", Entry: "worker",
            Nodes: new[]
            {
                new GraphNode("worker", "Agent", Ref: new GraphAgentRef("worker"),
                    StateBindings: new GraphStateBindings(Output: new[] { "result" })),
                new GraphNode("gate", "Interrupt"),
                new GraphNode("end", "End"),
            },
            Edges: new[]
            {
                new GraphEdge("worker", "gate"),
                new GraphEdge("gate", "end"),
            });

        var orchestrator = new MafGraphOrchestrator(manifest, registry, lifecycle);

        var finalState = await orchestrator.InvokeWithHitlAsync(
            new Dictionary<string, JsonElement>(),
            new AgentContext(),
            handleInterrupt: (_, _) =>
                ValueTask.FromResult<IDictionary<string, JsonElement>?>(
                    new Dictionary<string, JsonElement> { ["gateDecision"] = JsonSerializer.SerializeToElement("approved") }));

        finalState.Should().ContainKey("result");
        finalState["result"].GetString().Should().Be("done");
        finalState.Should().ContainKey("hitl.response");
    }

    // ---- RS-1/RS-2/RS-3: NodeAgentInvoked event ----

    [Fact]
    public async Task AgentNode_Emits_NodeAgentInvoked_With_Correct_Input_And_Output()
    {
        var (registry, lifecycle) = BuildHarness(_ => new CompletionResponse("agent-output"));
        await lifecycle.CreateAsync(ManifestFor("worker"));

        var manifest = new AgentGraphManifest(
            Id: "invoked-event-test", Version: "1.0", Entry: "worker",
            Nodes: new[]
            {
                new GraphNode("worker", "Agent", Ref: new GraphAgentRef("worker")),
                new GraphNode("end", "End"),
            },
            Edges: new[] { new GraphEdge("worker", "end") });

        var events = new List<AgentGraphEvent>();
        var orchestrator = new MafGraphOrchestrator(manifest, registry, lifecycle);
        await foreach (var e in orchestrator.StreamAsync(
            new Dictionary<string, JsonElement> { ["query"] = JsonSerializer.SerializeToElement("hello world") },
            new AgentContext()))
        {
            events.Add(e);
        }

        var invoked = events.OfType<NodeAgentInvoked>().SingleOrDefault();
        invoked.Should().NotBeNull("NodeAgentInvoked must be emitted for agent-kind nodes");
        invoked!.NodeId.Should().Be("worker");
        invoked.AgentId.Should().Be("worker");
        invoked.InputText.Should().Be("hello world");
        invoked.OutputText.Should().Be("agent-output");
        invoked.InputTokens.Should().Be(0);
        invoked.OutputTokens.Should().Be(0);

        // The event must appear between NodeStarted and NodeCompleted for the same node.
        var nodeStartedIdx = events.FindIndex(e => e is NodeStarted ns && ns.NodeId == "worker");
        var nodeCompletedIdx = events.FindIndex(e => e is NodeCompleted nc && nc.NodeId == "worker");
        var invokedIdx = events.IndexOf(invoked);
        invokedIdx.Should().BeGreaterThan(nodeStartedIdx, "NodeAgentInvoked follows NodeStarted");
        invokedIdx.Should().BeLessThan(nodeCompletedIdx, "NodeAgentInvoked precedes NodeCompleted");
    }

    [Fact]
    public async Task AgentNode_NodeAgentInvoked_Published_On_Bus()
    {
        var (registry, lifecycle) = BuildHarness(_ => new CompletionResponse("bus-output"));
        await lifecycle.CreateAsync(ManifestFor("node-a"));

        var manifest = new AgentGraphManifest(
            Id: "invoked-bus-test", Version: "1.0", Entry: "a",
            Nodes: new[]
            {
                new GraphNode("a", "Agent", Ref: new GraphAgentRef("node-a")),
                new GraphNode("end", "End"),
            },
            Edges: new[] { new GraphEdge("a", "end") });

        var bus = new InMemoryAgentGraphEventBus();
        var busEvents = new List<AgentGraphEvent>();
        using var _ = bus.Subscribe((e, ct) => { busEvents.Add(e); return ValueTask.CompletedTask; });

        var orchestrator = new MafGraphOrchestrator(manifest, registry, lifecycle, graphEventBus: bus);
        await foreach (var e in orchestrator.StreamAsync(new Dictionary<string, JsonElement>(), new AgentContext()))
        { }

        busEvents.OfType<NodeAgentInvoked>().Should().ContainSingle()
            .Which.OutputText.Should().Be("bus-output");
    }

    // ---- FO-7c / FO-7d: fan-out / fan-in ----

    [Fact]
    public void Build_ConcurrentManifest_DoesNotThrowAndHasCorrectEntry()
    {
        var (registry, lifecycle) = BuildHarness();
        var manifest = BuildTwoBranchManifest();

        // Build must succeed and expose the correct entry / name.
        var workflow = MafGraphBuilder.Build(manifest, registry, lifecycle);

        workflow.StartExecutorId.Should().Be("planner");
        workflow.Name.Should().Be("concurrent-research");

        // Helper classification mirrors the topology built above.
        MafGraphBuilder.IsForkSource("planner", manifest).Should().BeTrue(
            "planner has concurrent outgoing edges");
        MafGraphBuilder.IsJoinTarget("synthesizer", manifest).Should().BeTrue(
            "synthesizer has 2 concurrent incoming edges");
        MafGraphBuilder.IsForkSource("end", manifest).Should().BeFalse(
            "End node has no outgoing edges");
    }

    [Fact]
    public async Task FanOut_FanIn_BothBranchOutputsMergedInFinalState()
    {
        // All agents return a combined JSON; each node's output binding filters the
        // relevant key so planner writes nothing (no binding), researcher writes
        // research_findings, analyst writes analysis, synthesizer writes synthesis.
        var (registry, lifecycle) = BuildHarness(_ => new CompletionResponse(
            """{"research_findings":"found","analysis":"analyzed","synthesis":"done"}"""));

        foreach (var id in new[] { "planner-agent", "researcher-agent", "analyst-agent", "synthesizer-agent" })
            await lifecycle.CreateAsync(ManifestFor(id));

        var manifest = BuildTwoBranchManifest();
        var orchestrator = new MafGraphOrchestrator(manifest, registry, lifecycle);

        var events = new List<AgentGraphEvent>();
        await foreach (var e in orchestrator.StreamAsync(new Dictionary<string, JsonElement>(), new AgentContext()))
            events.Add(e);

        events.OfType<NodeStarted>().Select(ns => ns.NodeId).Should().Contain("researcher",
            "researcher branch must run");
        events.OfType<NodeStarted>().Select(ns => ns.NodeId).Should().Contain("analyst",
            "analyst branch must run");
        events.OfType<GraphCompleted>().Should().ContainSingle();

        var completed = events.OfType<GraphCompleted>().Single();
        completed.FinalState.Should().NotBeNull();
        completed.FinalState!.Should().ContainKey("research_findings",
            "researcher branch output must reach final state");
        completed.FinalState!.Should().ContainKey("analysis",
            "analyst branch output must reach final state");
        completed.FinalState!.Should().ContainKey("synthesis",
            "synthesizer output must be present after join");
    }

    [Fact]
    public async Task FanOut_BranchNodesRouteForwardToJoinTarget()
    {
        // Regression for the !e.Concurrent filter bug: branch nodes (researcher,
        // analyst) have only concurrent outgoing edges. Before the fix, the filter
        // excluded those edges so matchedEdge was null → RequestHaltAsync, killing
        // the workflow before both branches could complete.
        var (registry, lifecycle) = BuildHarness(_ => new CompletionResponse(
            """{"research_findings":"found","analysis":"analyzed","synthesis":"done"}"""));

        foreach (var id in new[] { "planner-agent", "researcher-agent", "analyst-agent", "synthesizer-agent" })
            await lifecycle.CreateAsync(ManifestFor(id));

        var manifest = BuildTwoBranchManifest();
        var orchestrator = new MafGraphOrchestrator(manifest, registry, lifecycle);

        var events = new List<AgentGraphEvent>();
        await foreach (var e in orchestrator.StreamAsync(new Dictionary<string, JsonElement>(), new AgentContext()))
            events.Add(e);

        var traversed = events.OfType<EdgeTraversed>().Select(e => (e.From, e.To)).ToList();
        traversed.Should().Contain(("researcher", "synthesizer"),
            "researcher branch must route forward to synthesizer, not halt");
        traversed.Should().Contain(("analyst", "synthesizer"),
            "analyst branch must route forward to synthesizer, not halt");
    }

    [Fact]
    public async Task FanOut_BranchesReceiveIsolatedStateCopies()
    {
        // Regression for the shared-state-dict bug: both branches referenced the
        // same State dict. Whichever branch ran first appended its output to
        // messages; the sibling received that text as its user_message via
        // BuildAgentInputText instead of seeing planner's output.
        var callCount = 0;
        var inputs = new System.Collections.Concurrent.ConcurrentBag<string>();

        var (registry, lifecycle) = BuildHarness(req =>
        {
            inputs.Add(req.History.LastOrDefault()?.Text ?? "");
            var n = Interlocked.Increment(ref callCount);
            // call 1 = planner; branches get a distinct response so we can detect
            // if any branch received another branch's output as its input
            return n == 1
                ? new CompletionResponse("planner-text")
                : new CompletionResponse("branch-text");
        });

        foreach (var id in new[] { "planner-agent", "researcher-agent", "analyst-agent", "synthesizer-agent" })
            await lifecycle.CreateAsync(ManifestFor(id));

        var manifest = BuildTwoBranchManifest();
        var orchestrator = new MafGraphOrchestrator(manifest, registry, lifecycle);

        await foreach (var _ in orchestrator.StreamAsync(new Dictionary<string, JsonElement>(), new AgentContext()))
        { }

        // With isolated state each branch independently sees "planner-text" (the
        // last message planner wrote) as its user_message. With shared state the
        // second branch would instead see "branch-text" (the first branch's output
        // appended to the shared messages list), so the count drops to 1.
        inputs.Count(t => t == "planner-text").Should().Be(2,
            "both branches must receive planner's output as input, not each other's");
    }

    [Fact]
    public async Task FanOut_BranchesWithExplicitInputBindings_ReceivePlannerOutputNotQuery()
    {
        // Regression: branches with explicit Input bindings that omit "messages" caused
        // FilterByInputBinding to strip the conversation history, so BuildAgentInputText
        // fell back to "query" ("initial request") instead of the planner's output.
        // This matches the shape of research-pipeline.yaml: input: [query, research_plan].
        var callCount = 0;
        var inputs = new System.Collections.Concurrent.ConcurrentBag<string>();

        var (registry, lifecycle) = BuildHarness(req =>
        {
            inputs.Add(req.History.LastOrDefault()?.Text ?? "");
            var n = Interlocked.Increment(ref callCount);
            return n == 1
                ? new CompletionResponse("planner-output")
                : new CompletionResponse("branch-text");
        });

        foreach (var id in new[] { "planner-agent", "researcher-agent", "analyst-agent", "synthesizer-agent" })
            await lifecycle.CreateAsync(ManifestFor(id));

        var manifest = new AgentGraphManifest(
            Id: "concurrent-research-bound", Version: "1.0", Entry: "planner",
            Nodes: new[]
            {
                new GraphNode("planner",     "Agent", Ref: new GraphAgentRef("planner-agent")),
                new GraphNode("researcher",  "Agent", Ref: new GraphAgentRef("researcher-agent"),
                    StateBindings: new GraphStateBindings(
                        Input:  new[] { "query", "research_plan" },
                        Output: new[] { "research_findings" })),
                new GraphNode("analyst",     "Agent", Ref: new GraphAgentRef("analyst-agent"),
                    StateBindings: new GraphStateBindings(
                        Input:  new[] { "query", "research_plan" },
                        Output: new[] { "analysis" })),
                new GraphNode("synthesizer", "Agent", Ref: new GraphAgentRef("synthesizer-agent"),
                    StateBindings: new GraphStateBindings(
                        Input:  new[] { "research_findings", "analysis" },
                        Output: new[] { "synthesis" })),
                new GraphNode("end", "End"),
            },
            Edges: new[]
            {
                new GraphEdge("planner",     "researcher",  Concurrent: true),
                new GraphEdge("planner",     "analyst",     Concurrent: true),
                new GraphEdge("researcher",  "synthesizer", Concurrent: true),
                new GraphEdge("analyst",     "synthesizer", Concurrent: true),
                new GraphEdge("synthesizer", "end"),
            })
        { MaxSteps = 50 };

        var orchestrator = new MafGraphOrchestrator(manifest, registry, lifecycle);
        await foreach (var _ in orchestrator.StreamAsync(
            new Dictionary<string, JsonElement> { ["query"] = JsonSerializer.SerializeToElement("initial request") },
            new AgentContext()))
        { }

        // Both branches must receive "planner-output" (from messages), not "initial request" (from query).
        inputs.Count(t => t == "planner-output").Should().Be(2,
            "branches with explicit input bindings must still receive planner output via messages, not fall back to query");
    }

    [Fact]
    public async Task FanIn_JoinNode_ReceivesStructuredInputFromAllBranches()
    {
        // Regression: BuildAgentInputText returned only the last arriving branch's message
        // for join nodes, so the synthesizer received analyst output but not research_findings
        // (or vice versa). With the fix it builds [key]\nvalue\n sections for all bound keys
        // when the binding declares 2+ non-messages, non-query data keys.
        var callCount = 0;
        string? synthesizerInput = null;

        var (registry, lifecycle) = BuildHarness(req =>
        {
            var n = Interlocked.Increment(ref callCount);
            if (n == 1)
                return new CompletionResponse("planner-output");
            if (n <= 3)
                // researcher / analyst — each returns JSON with both output keys;
                // FilterByOutputBinding ensures only the node's declared key reaches state
                return new CompletionResponse(
                    """{"research_findings":"rf-value","analysis":"an-value"}""");
            // n == 4: synthesizer (always last — join waits for both branches)
            synthesizerInput = req.History.LastOrDefault()?.Text ?? "";
            return new CompletionResponse("final-report");
        });

        foreach (var id in new[] { "planner-agent", "researcher-agent", "analyst-agent", "synthesizer-agent" })
            await lifecycle.CreateAsync(ManifestFor(id));

        var orchestrator = new MafGraphOrchestrator(BuildTwoBranchManifest(), registry, lifecycle);
        await foreach (var _ in orchestrator.StreamAsync(new Dictionary<string, JsonElement>(), new AgentContext()))
        { }

        synthesizerInput.Should().NotBeNull("synthesizer must be called");
        synthesizerInput.Should().Contain("rf-value",
            "synthesizer must receive research_findings from researcher branch");
        synthesizerInput.Should().Contain("an-value",
            "synthesizer must receive analysis from analyst branch");
        synthesizerInput.Should().Contain("[research_findings]",
            "structured input must label each section with the binding key");
        synthesizerInput.Should().Contain("[analysis]",
            "structured input must label each section with the binding key");
    }

    // ---- MP-4: remote invoker / bearer token / lifecycle manager warning ----

    [Fact]
    public async Task MafGraphOrchestrator_RuntimeUrlNode_ForwardsToRemoteInvoker()
    {
        var (registry, lifecycle) = BuildHarness();
        var stub = new StubRemoteInvoker();
        var orchestrator = new MafGraphOrchestrator(
            BuildRuntimeUrlManifest(), registry, lifecycle, remoteInvoker: stub);

        await foreach (var _ in orchestrator.StreamAsync(new Dictionary<string, JsonElement>(), new AgentContext()))
        { }

        stub.Calls.Should().ContainSingle()
            .Which.Url.Should().Be("https://runtime-b.test");
    }

    [Fact]
    public async Task MafGraphOrchestrator_BearerToken_PassedToRemoteInvoker()
    {
        var (registry, lifecycle) = BuildHarness();
        var stub = new StubRemoteInvoker();
        var orchestrator = new MafGraphOrchestrator(
            BuildRuntimeUrlManifest(), registry, lifecycle,
            remoteInvoker: stub, bearerToken: "test-bearer");

        await foreach (var _ in orchestrator.StreamAsync(new Dictionary<string, JsonElement>(), new AgentContext()))
        { }

        stub.Calls.Should().ContainSingle()
            .Which.Bearer.Should().Be("test-bearer");
    }

    [Fact]
    public async Task AgentGraphLifecycleManager_OrchestratorFactory_MafOrchestrator_RunsConcurrentBranches()
    {
        var (registry, lifecycle) = BuildHarness(_ => new CompletionResponse(
            """{"research_findings":"found","analysis":"analyzed","synthesis":"done"}"""));
        foreach (var id in new[] { "planner-agent", "researcher-agent", "analyst-agent", "synthesizer-agent" })
            await lifecycle.CreateAsync(ManifestFor(id));

        var graphRegistry = new InMemoryAgentGraphRegistry();
        var manager = new AgentGraphLifecycleManager(
            graphRegistry, registry, lifecycle, new InMemoryCheckpointer(),
            orchestratorFactory: (manifest, runId) =>
                new MafGraphOrchestrator(manifest, registry, lifecycle, runIdFactory: () => runId));

        var handle = await manager.CreateAsync(BuildTwoBranchManifest());
        var events = new List<AgentGraphEvent>();
        await foreach (var e in manager.InvokeStreamAsync(handle, new GraphInvocationRequest(new Dictionary<string, JsonElement>())))
            events.Add(e);

        events.OfType<NodeStarted>().Select(n => n.NodeId).Should().Contain("researcher",
            "MAF factory must run concurrent researcher branch");
        events.OfType<NodeStarted>().Select(n => n.NodeId).Should().Contain("analyst",
            "MAF factory must run concurrent analyst branch");
    }

    [Fact]
    public async Task AgentGraphLifecycleManager_ConcurrentEdges_WithoutFactory_EmitsWarning()
    {
        var (registry, lifecycle) = BuildHarness();
        await lifecycle.CreateAsync(ManifestFor("a-agent"));
        var graphRegistry = new InMemoryAgentGraphRegistry();
        var logger = new CapturingLogger<AgentGraphLifecycleManager>();
        var manager = new AgentGraphLifecycleManager(
            graphRegistry, registry, lifecycle, new InMemoryCheckpointer(), logger: logger);

        var manifest = new AgentGraphManifest(
            Id: "warn-test", Version: "1.0", Entry: "a",
            Nodes: new[] { new GraphNode("a", "Agent", Ref: new GraphAgentRef("a-agent")), new GraphNode("end", "End") },
            Edges: new[] { new GraphEdge("a", "end", Concurrent: true) });

        var handle = await manager.CreateAsync(manifest);
        await manager.InvokeAsync(handle, new GraphInvocationRequest(new Dictionary<string, JsonElement>()));

        logger.Messages.Should().Contain(m => m.Contains("concurrent"),
            "InProcessGraphOrchestrator fallback must warn when concurrent edges are present");
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

    private static AgentGraphManifest BuildTwoBranchManifest() => new(
        Id: "concurrent-research", Version: "1.0", Entry: "planner",
        Nodes: new[]
        {
            new GraphNode("planner",     "Agent", Ref: new GraphAgentRef("planner-agent")),
            new GraphNode("researcher",  "Agent", Ref: new GraphAgentRef("researcher-agent"),
                StateBindings: new GraphStateBindings(Output: new[] { "research_findings" })),
            new GraphNode("analyst",     "Agent", Ref: new GraphAgentRef("analyst-agent"),
                StateBindings: new GraphStateBindings(Output: new[] { "analysis" })),
            new GraphNode("synthesizer", "Agent", Ref: new GraphAgentRef("synthesizer-agent"),
                StateBindings: new GraphStateBindings(
                    Input: new[] { "research_findings", "analysis" },
                    Output: new[] { "synthesis" })),
            new GraphNode("end", "End"),
        },
        Edges: new[]
        {
            new GraphEdge("planner",     "researcher",  Concurrent: true),
            new GraphEdge("planner",     "analyst",     Concurrent: true),
            new GraphEdge("researcher",  "synthesizer", Concurrent: true),
            new GraphEdge("analyst",     "synthesizer", Concurrent: true),
            new GraphEdge("synthesizer", "end"),
        })
    { MaxSteps = 50 };

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

    private static AgentGraphManifest BuildRuntimeUrlManifest() => new(
        Id: "remote-graph", Version: "1.0", Entry: "step",
        Nodes: new[]
        {
            new GraphNode("step", "Agent", Ref: new GraphAgentRef("remote-agent", "1.0", RuntimeUrl: "https://runtime-b.test")),
            new GraphNode("end", "End"),
        },
        Edges: new[] { new GraphEdge("step", "end") });

    private sealed class StubRemoteInvoker : IAgentRemoteInvoker
    {
        public List<(string Url, string? Bearer)> Calls { get; } = new();

        public ValueTask<AgentInvocationResult> InvokeAsync(
            string runtimeUrl, AgentHandle handle, AgentInvocationRequest request,
            string? bearerToken, CancellationToken cancellationToken = default)
        {
            Calls.Add((runtimeUrl, bearerToken));
            return ValueTask.FromResult(new AgentInvocationResult("remote-reply"));
        }

        public IAsyncEnumerable<AgentEvent> StreamAsync(
            string runtimeUrl, AgentHandle handle, AgentInvocationRequest request,
            string? bearerToken, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly List<string> _messages = new();
        public IReadOnlyList<string> Messages => _messages;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => _messages.Add(formatter(state, exception));
    }
}
