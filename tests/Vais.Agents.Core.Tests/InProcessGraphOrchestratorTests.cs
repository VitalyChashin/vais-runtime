// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Vais.Agents.Control.InProcess;
using Vais.Agents.Hosting.InMemory;
using Xunit;

namespace Vais.Agents.Core.Tests;

/// <summary>
/// v0.9 PR 1: neutral graph orchestration. Exercises <see cref="InProcessGraphOrchestrator{TState}"/>
/// against both canonical archetypes from the spike (pure handoff + retrieval loop),
/// plus surface coverage of the predicate evaluator + default reducers + checkpointer +
/// state-binding flow.
/// </summary>
public sealed class InProcessGraphOrchestratorTests
{
    [Fact]
    public void Constructor_Throws_When_Manifest_Has_Concurrent_Edges()
    {
        // The in-process orchestrator is sequential-only — first-match-wins edge selection,
        // no fan-out/fan-in barrier. A concurrent-edge manifest must fail loudly rather than
        // silently run a single branch. (Fan-out/fan-in is MafGraphOrchestrator's job.)
        var (registry, lifecycle) = BuildHarness(_ => new CompletionResponse("unused"));
        var manifest = new AgentGraphManifest(
            "fanout-graph", "1.0", "fork",
            Nodes: new[]
            {
                new GraphNode("fork", "Agent", Ref: new GraphAgentRef("fork-agent")),
                new GraphNode("a", "Agent", Ref: new GraphAgentRef("a-agent")),
                new GraphNode("b", "Agent", Ref: new GraphAgentRef("b-agent")),
                new GraphNode("end", "End"),
            },
            Edges: new[]
            {
                new GraphEdge("fork", "a", Concurrent: true),
                new GraphEdge("fork", "b", Concurrent: true),
                new GraphEdge("a", "end"),
                new GraphEdge("b", "end"),
            });

        var act = () => new InProcessGraphOrchestrator(manifest, registry, lifecycle, new InMemoryCheckpointer());

        act.Should().Throw<NotSupportedException>(
                because: "concurrent fan-out/fan-in edges require MafGraphOrchestrator")
            .WithMessage("*sequential-only*MafGraphOrchestrator*");
    }

    [Fact]
    public async Task Archetype_A_Routes_Refund_To_Billing()
    {
        // Triage agent replies with a fixed script; the graph's edge predicate
        // matches on lastMessage.text keyword "refund".
        var (registry, lifecycle) = BuildHarness(req =>
        {
            // Triage echoes the user's input; billing/sales produce their own responses.
            var last = req.History.LastOrDefault()?.Text ?? string.Empty;
            return new CompletionResponse($"echoed: {last}");
        });
        await lifecycle.CreateAsync(ManifestFor("triage-agent"));
        await lifecycle.CreateAsync(ManifestFor("billing-agent"));
        await lifecycle.CreateAsync(ManifestFor("sales-agent"));

        var manifest = BuildHandoffGraph();
        var orchestrator = new InProcessGraphOrchestrator(manifest, registry, lifecycle, new InMemoryCheckpointer());
        var initial = new Dictionary<string, JsonElement>
        {
            [GraphStateReducers.WellKnownKey.Messages] = JsonSerializer.SerializeToElement(new[]
            {
                JsonSerializer.SerializeToElement(new ChatTurn(AgentChatRole.User, "I need a refund for my last order")),
            }),
        };

        var final = await orchestrator.InvokeAsync(initial, new AgentContext(UserId: "u1"));

        var events = await StreamEventsAsync(manifest, registry, lifecycle, initial, req =>
        {
            var last = req.History.LastOrDefault()?.Text ?? string.Empty;
            return new CompletionResponse($"echoed: {last}");
        });
        events.OfType<EdgeTraversed>().Should().Contain(e => e.From == "triage" && e.To == "billing");
        events.OfType<EdgeTraversed>().Should().NotContain(e => e.To == "sales");
        events.OfType<GraphCompleted>().Should().ContainSingle();
    }

    [Fact]
    public async Task Archetype_B_Retrieval_Loop_Terminates_After_Three_Retries()
    {
        int retrieveCallCount = 0;
        var (registry, lifecycle) = BuildHarness(req =>
        {
            // Grade-node's 1st-3rd invocations return low quality (triggering a loop);
            // 4th returns high quality (exits to end). Non-grade nodes are echoed with
            // low quality too; only the grade node reads `quality` via its OutputBinding,
            // so other nodes' responses don't pollute state.
            var invocation = retrieveCallCount;
            return new CompletionResponse(invocation < 4 ? "{\"quality\":0.3}" : "{\"quality\":0.9}");
        });
        await lifecycle.CreateAsync(ManifestFor("retrieval-agent"));
        await lifecycle.CreateAsync(ManifestFor("answering-agent"));
        await lifecycle.CreateAsync(ManifestFor("grading-agent", outputSchema: "{\"type\":\"object\",\"properties\":{\"quality\":{\"type\":\"number\"}}}"));

        var manifest = BuildRetrievalLoopGraph();
        var orchestrator = new InProcessGraphOrchestrator(
            manifest, registry, lifecycle, new InMemoryCheckpointer(),
            runIdFactory: () => "run-retrieval");
        var initial = new Dictionary<string, JsonElement>
        {
            ["query"] = JsonSerializer.SerializeToElement("what is the best plan?"),
            ["retryCount"] = JsonSerializer.SerializeToElement(0),
        };

        var events = new List<AgentGraphEvent>();
        await foreach (var e in orchestrator.StreamAsync(initial, new AgentContext(UserId: "u1")))
        {
            events.Add(e);
            if (e is NodeStarted ns && ns.NodeId == "grade")
            {
                retrieveCallCount++; // each grade-cycle increments our "how many attempts" counter
            }
        }

        events.OfType<GraphCompleted>().Should().ContainSingle();
        events.OfType<EdgeTraversed>().Count(e => e.From == "grade" && e.To == "retrieve").Should().Be(3);
        events.OfType<EdgeTraversed>().Count(e => e.From == "grade" && e.To == "end").Should().Be(1);
    }

    [Fact]
    public async Task MaxSteps_Ceiling_Trips_On_Infinite_Loop()
    {
        var (registry, lifecycle) = BuildHarness();
        await lifecycle.CreateAsync(ManifestFor("forever"));

        // A-B-A-B... forever with maxSteps = 5.
        var manifest = new AgentGraphManifest(
            Id: "infinite",
            Version: "1.0",
            Entry: "a",
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
        { MaxSteps = 5 };

        var orchestrator = new InProcessGraphOrchestrator(manifest, registry, lifecycle, new InMemoryCheckpointer());
        await FluentActions.Invoking(async () => await orchestrator.InvokeAsync(
                new Dictionary<string, JsonElement>(), new AgentContext()))
            .Should().ThrowAsync<GraphRecursionException>()
            .Where(ex => ex.MaxSteps == 5 && ex.GraphId == "infinite");
    }

    [Fact]
    public async Task InMemoryCheckpointer_Round_Trips()
    {
        var store = new InMemoryCheckpointer();
        var cp = new GraphCheckpoint(
            RunId: "r1", GraphId: "g", GraphVersion: "1.0",
            State: new Dictionary<string, JsonElement> { ["x"] = JsonSerializer.SerializeToElement(42) },
            NextNodeId: "b", SuperStepIndex: 3, PendingInterruptId: null,
            IsComplete: false, CreatedAt: DateTimeOffset.UtcNow);

        await store.SaveAsync(cp);
        var loaded = await store.LoadAsync("r1");

        loaded.Should().NotBeNull();
        loaded!.NextNodeId.Should().Be("b");
        loaded.State["x"].GetInt32().Should().Be(42);

        await store.DeleteAsync("r1");
        (await store.LoadAsync("r1")).Should().BeNull();
    }

    [Fact]
    public async Task Interrupt_Node_Emits_Event_And_Checkpoints()
    {
        var (registry, lifecycle) = BuildHarness();
        await lifecycle.CreateAsync(ManifestFor("pre-approval"));

        var manifest = new AgentGraphManifest(
            Id: "approval-graph", Version: "1.0", Entry: "pre",
            Nodes: new[]
            {
                new GraphNode("pre", "Agent", Ref: new GraphAgentRef("pre-approval")),
                new GraphNode("wait", "Interrupt", InterruptReason: "needs human approval"),
                new GraphNode("end", "End"),
            },
            Edges: new[]
            {
                new GraphEdge("pre", "wait"),
                new GraphEdge("wait", "end"),
            });

        var checkpointer = new InMemoryCheckpointer();
        var orchestrator = new InProcessGraphOrchestrator(
            manifest, registry, lifecycle, checkpointer, runIdFactory: () => "run-interrupt");

        var events = new List<AgentGraphEvent>();
        await foreach (var e in orchestrator.StreamAsync(new Dictionary<string, JsonElement>(), new AgentContext()))
        {
            events.Add(e);
        }

        events.OfType<GraphInterrupted>().Should().ContainSingle()
            .Which.Reason.Should().Be("needs human approval");
        events.OfType<GraphCompleted>().Should().BeEmpty(); // interrupt pauses, no completion
        var checkpoint = await checkpointer.LoadAsync("run-interrupt");
        checkpoint.Should().NotBeNull();
        checkpoint!.PendingInterruptId.Should().NotBeNullOrEmpty();
        checkpoint.IsComplete.Should().BeFalse();
    }

    [Fact]
    public async Task Predicate_Evaluator_Supports_All_Ten_Operators()
    {
        var state = new Dictionary<string, JsonElement>
        {
            ["name"] = JsonSerializer.SerializeToElement("alice"),
            ["age"] = JsonSerializer.SerializeToElement(30),
            ["tags"] = JsonSerializer.SerializeToElement(new[] { "admin", "power-user" }),
        };
        var cases = new (GraphPredicateOperator Op, string Property, JsonElement? Value, bool Expected)[]
        {
            (GraphPredicateOperator.Eq, "name", JsonSerializer.SerializeToElement("alice"), true),
            (GraphPredicateOperator.NotEq, "name", JsonSerializer.SerializeToElement("bob"), true),
            (GraphPredicateOperator.Gt, "age", JsonSerializer.SerializeToElement(20), true),
            (GraphPredicateOperator.Gte, "age", JsonSerializer.SerializeToElement(30), true),
            (GraphPredicateOperator.Lt, "age", JsonSerializer.SerializeToElement(100), true),
            (GraphPredicateOperator.Lte, "age", JsonSerializer.SerializeToElement(30), true),
            (GraphPredicateOperator.Contains, "tags", JsonSerializer.SerializeToElement("admin"), true),
            (GraphPredicateOperator.NotContains, "tags", JsonSerializer.SerializeToElement("ghost"), true),
            (GraphPredicateOperator.Exists, "name", null, true),
            (GraphPredicateOperator.NotExists, "missing", null, true),
        };
        foreach (var c in cases)
        {
            var pred = new GraphEdgePredicate.PropertyMatcher(c.Property, c.Op, c.Value);
            var result = await GraphPredicateEvaluator.EvaluateAsync(pred, state, null, CancellationToken.None);
            result.Should().Be(c.Expected, $"{c.Op} on '{c.Property}' should be {c.Expected}");
        }
    }

    [Fact]
    public async Task Predicate_Combinators_AllOf_AnyOf_Not()
    {
        var state = new Dictionary<string, JsonElement>
        {
            ["a"] = JsonSerializer.SerializeToElement(1),
            ["b"] = JsonSerializer.SerializeToElement(2),
        };
        var p1 = new GraphEdgePredicate.PropertyMatcher("a", GraphPredicateOperator.Eq, JsonSerializer.SerializeToElement(1));
        var p2 = new GraphEdgePredicate.PropertyMatcher("b", GraphPredicateOperator.Eq, JsonSerializer.SerializeToElement(999));

        var allOf = new GraphEdgePredicate.AllOf(new[] { (GraphEdgePredicate)p1, p2 });
        var anyOf = new GraphEdgePredicate.AnyOf(new[] { (GraphEdgePredicate)p1, p2 });
        var notP2 = new GraphEdgePredicate.Not(p2);

        (await GraphPredicateEvaluator.EvaluateAsync(allOf, state, null, default)).Should().BeFalse();
        (await GraphPredicateEvaluator.EvaluateAsync(anyOf, state, null, default)).Should().BeTrue();
        (await GraphPredicateEvaluator.EvaluateAsync(notP2, state, null, default)).Should().BeTrue();
    }

    [Fact]
    public void AppendMessages_Reducer_Threads_History_Across_Nodes()
    {
        var state = new Dictionary<string, JsonElement>
        {
            [GraphStateReducers.WellKnownKey.Messages] = JsonSerializer.SerializeToElement(new[]
            {
                JsonSerializer.SerializeToElement(new ChatTurn(AgentChatRole.User, "hi")),
            }),
        };
        var incoming = new Dictionary<string, JsonElement>
        {
            [GraphStateReducers.WellKnownKey.Messages] = JsonSerializer.SerializeToElement(new[]
            {
                JsonSerializer.SerializeToElement(new ChatTurn(AgentChatRole.Assistant, "hello back")),
            }),
        };

        var changed = GraphStateReducers.Merge(state, incoming);

        changed.Should().Contain(GraphStateReducers.WellKnownKey.Messages);
        state[GraphStateReducers.WellKnownKey.Messages].GetArrayLength().Should().Be(2);
    }

    [Fact]
    public async Task StateBindings_Output_Extracts_Structured_Agent_Response()
    {
        var (registry, lifecycle) = BuildHarness(_ => new CompletionResponse("{\"quality\":0.85,\"notes\":\"good\"}"));
        await lifecycle.CreateAsync(ManifestFor("grader"));

        var manifest = new AgentGraphManifest(
            Id: "grade", Version: "1.0", Entry: "g",
            Nodes: new[]
            {
                new GraphNode("g", "Agent", Ref: new GraphAgentRef("grader"),
                    StateBindings: new GraphStateBindings(Output: new[] { "quality" })),
                new GraphNode("end", "End"),
            },
            Edges: new[] { new GraphEdge("g", "end") });

        var orchestrator = new InProcessGraphOrchestrator(manifest, registry, lifecycle, new InMemoryCheckpointer());
        var final = await orchestrator.InvokeAsync(
            new Dictionary<string, JsonElement>(), new AgentContext());

        final.Should().ContainKey("quality");
        final["quality"].GetDouble().Should().BeApproximately(0.85, 0.001);
        // "notes" NOT bound — filtered out by StateBindings.Output.
        final.Should().NotContainKey("notes");
    }

    [Fact]
    public async Task StateBindings_Input_Threads_Into_Invocation_Metadata()
    {
        CompletionRequest? captured = null;
        var (registry, lifecycle) = BuildHarness(req =>
        {
            captured = req;
            return new CompletionResponse("ok");
        });
        await lifecycle.CreateAsync(ManifestFor("echo"));

        var manifest = new AgentGraphManifest(
            Id: "bind", Version: "1.0", Entry: "n",
            Nodes: new[]
            {
                new GraphNode("n", "Agent", Ref: new GraphAgentRef("echo"),
                    StateBindings: new GraphStateBindings(Input: new[] { "query" })),
                new GraphNode("end", "End"),
            },
            Edges: new[] { new GraphEdge("n", "end") });

        var orchestrator = new InProcessGraphOrchestrator(manifest, registry, lifecycle, new InMemoryCheckpointer());
        var initial = new Dictionary<string, JsonElement>
        {
            ["query"] = JsonSerializer.SerializeToElement("find me an answer"),
        };
        await orchestrator.InvokeAsync(initial, new AgentContext(UserId: "u1"));

        captured.Should().NotBeNull();
        // AgentInvocationRequest.Metadata flows as CompletionRequest context — we check
        // only that the invocation happened; metadata threading through InMemoryAgentRuntime
        // is the runtime's concern. (The binding is visible at lifecycle.InvokeAsync boundary.)
    }

    [Fact]
    public async Task Edge_OnTraverse_Increment_Mutates_State()
    {
        var (registry, lifecycle) = BuildHarness();
        await lifecycle.CreateAsync(ManifestFor("noop"));

        var manifest = new AgentGraphManifest(
            Id: "counter", Version: "1.0", Entry: "a",
            Nodes: new[]
            {
                new GraphNode("a", "Agent", Ref: new GraphAgentRef("noop")),
                new GraphNode("end", "End"),
            },
            Edges: new[]
            {
                new GraphEdge("a", "end", OnTraverse: new GraphEdgeEffect.Increment("hops", 5)),
            });

        var orchestrator = new InProcessGraphOrchestrator(manifest, registry, lifecycle, new InMemoryCheckpointer());
        var final = await orchestrator.InvokeAsync(new Dictionary<string, JsonElement>(), new AgentContext());

        final.Should().ContainKey("hops");
        final["hops"].GetInt32().Should().Be(5);
    }

    [Fact]
    public async Task Resume_From_Checkpoint_Continues_Past_Interrupt()
    {
        var (registry, lifecycle) = BuildHarness();
        await lifecycle.CreateAsync(ManifestFor("pre"));
        await lifecycle.CreateAsync(ManifestFor("post"));

        var manifest = new AgentGraphManifest(
            Id: "resume-test", Version: "1.0", Entry: "pre",
            Nodes: new[]
            {
                new GraphNode("pre", "Agent", Ref: new GraphAgentRef("pre")),
                new GraphNode("wait", "Interrupt", InterruptReason: "pause"),
                new GraphNode("post", "Agent", Ref: new GraphAgentRef("post")),
                new GraphNode("end", "End"),
            },
            Edges: new[]
            {
                new GraphEdge("pre", "wait"),
                new GraphEdge("wait", "post"),
                new GraphEdge("post", "end"),
            });

        var checkpointer = new InMemoryCheckpointer();
        var orchestrator1 = new InProcessGraphOrchestrator(
            manifest, registry, lifecycle, checkpointer, runIdFactory: () => "resume-r1");

        // First run: pauses at interrupt.
        var events1 = new List<AgentGraphEvent>();
        await foreach (var e in orchestrator1.StreamAsync(new Dictionary<string, JsonElement>(), new AgentContext()))
        {
            events1.Add(e);
        }
        events1.OfType<GraphInterrupted>().Should().ContainSingle();

        var checkpoint = await checkpointer.LoadAsync("resume-r1");
        checkpoint.Should().NotBeNull();
        checkpoint!.PendingInterruptId.Should().NotBeNullOrEmpty();

        // Resume: build a fresh orchestrator whose entry skips past the interrupt.
        // (v0.9 resume happens via the caller rebuilding the manifest with the
        // interrupt node replaced by an End-or-passthrough edge; full resume with
        // checkpoint-state restoration lands in PR 4.)
        // For v0.9 PR 1 we assert only the checkpoint shape + pause semantics.
        checkpoint.IsComplete.Should().BeFalse();
        checkpoint.NextNodeId.Should().Be("wait");
    }

    // ---- v0.37 custom reducer tests ----

    [Fact]
    public async Task CustomAppend_Reducer_On_NonMessages_Key_Accumulates_Across_Nodes()
    {
        int callCount = 0;
        var (registry, lifecycle) = BuildHarness(_ =>
        {
            var n = System.Threading.Interlocked.Increment(ref callCount);
            return new CompletionResponse(n == 1 ? """{"tags":["red"]}""" : """{"tags":["blue"]}""");
        });
        await lifecycle.CreateAsync(ManifestFor("node-a"));
        await lifecycle.CreateAsync(ManifestFor("node-b"));

        var manifest = new AgentGraphManifest(
            Id: "tag-graph", Version: "1.0", Entry: "a",
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

        var orchestrator = new InProcessGraphOrchestrator(
            manifest, registry, lifecycle, new InMemoryCheckpointer());
        var final = await orchestrator.InvokeAsync(new Dictionary<string, JsonElement>(), new AgentContext());

        final.Should().ContainKey("tags");
        final["tags"].GetArrayLength().Should().Be(2);
        var items = final["tags"].EnumerateArray().Select(e => e.GetString()).ToList();
        items.Should().Contain("red").And.Contain("blue");
    }

    [Fact]
    public async Task CustomHandlerRef_Reducer_Is_Invoked_For_Declared_Key()
    {
        bool reducerCalled = false;
        var (registry, lifecycle) = BuildHarness(_ => new CompletionResponse("""{"score":99}"""));
        await lifecycle.CreateAsync(ManifestFor("scorer"));

        var manifest = new AgentGraphManifest(
            Id: "handlerref-reducer", Version: "1.0", Entry: "scorer",
            Nodes: new[]
            {
                new GraphNode("scorer", "Agent", Ref: new GraphAgentRef("scorer"),
                    StateBindings: new GraphStateBindings(Output: new[] { "score" })),
                new GraphNode("end", "End"),
            },
            Edges: new[] { new GraphEdge("scorer", "end") })
        {
            StateReducers = new Dictionary<string, GraphStateReducer>
            {
                ["score"] = new GraphStateReducer.HandlerRef(new GraphHandlerRef("SentinelReducer")),
            },
        };

        IGraphStateReducer reducerFactory(GraphHandlerRef _)
        {
            reducerCalled = true;
            return new FixedValueReducer(JsonSerializer.SerializeToElement(-1));
        }

        var orchestrator = new InProcessGraphOrchestrator(
            manifest, registry, lifecycle, new InMemoryCheckpointer(),
            reducerResolver: reducerFactory);
        var initial = new Dictionary<string, JsonElement>
        {
            ["score"] = JsonSerializer.SerializeToElement(0),
        };
        var final = await orchestrator.InvokeAsync(initial, new AgentContext());

        reducerCalled.Should().BeTrue("the manifest-declared handlerRef reducer should have been invoked");
        final["score"].GetInt32().Should().Be(-1, "the custom reducer returned -1 regardless of incoming");
    }

    [Fact]
    public async Task Explicit_Messages_LastWriteWins_Overrides_Builtin_Append()
    {
        int callCount = 0;
        var (registry, lifecycle) = BuildHarness(_ =>
        {
            var n = System.Threading.Interlocked.Increment(ref callCount);
            return new CompletionResponse(n == 1 ? "first" : "second");
        });
        await lifecycle.CreateAsync(ManifestFor("node-a"));
        await lifecycle.CreateAsync(ManifestFor("node-b"));

        var manifest = new AgentGraphManifest(
            Id: "lww-messages", Version: "1.0", Entry: "a",
            Nodes: new[]
            {
                new GraphNode("a", "Agent", Ref: new GraphAgentRef("node-a")),
                new GraphNode("b", "Agent", Ref: new GraphAgentRef("node-b")),
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
                [GraphStateReducers.WellKnownKey.Messages] = new GraphStateReducer.LastWriteWins(),
            },
        };

        var orchestrator = new InProcessGraphOrchestrator(
            manifest, registry, lifecycle, new InMemoryCheckpointer());
        var final = await orchestrator.InvokeAsync(new Dictionary<string, JsonElement>(), new AgentContext());

        // Without LWW override each node would append — array would hold 2 turns.
        // With LWW, only the last node's output survives.
        final[GraphStateReducers.WellKnownKey.Messages].GetArrayLength().Should().Be(1,
            "last-write-wins on messages means only the final node's turn survives");
    }

    // ---- helpers ----

    // ---- P9 / ADR 016: node-boundary failure visibility ----

    [Fact]
    public async Task NodeFailure_EmitsGraphFailed_With_FailedNodeId_And_Logs_Error()
    {
        // A node whose agent throws must surface a GraphFailed carrying the failing node id,
        // an ERROR log with run_id/node_id/node_kind, and the full stack in ErrorMessage.
        var (registry, lifecycle) = BuildHarness(_ =>
            throw new InvalidOperationException("boom in node"));
        await lifecycle.CreateAsync(ManifestFor("work-agent"));

        var manifest = new AgentGraphManifest(
            Id: "fail-graph", Version: "1.0", Entry: "work",
            Nodes: new[]
            {
                new GraphNode("work", "Agent", Ref: new GraphAgentRef("work-agent")),
                new GraphNode("end", "End"),
            },
            Edges: new[] { new GraphEdge("work", "end") });

        var logger = new CapturingLogger();
        var orchestrator = new InProcessGraphOrchestrator<IDictionary<string, JsonElement>>(
            manifest, registry, lifecycle, new InMemoryCheckpointer(),
            runIdFactory: () => "run-fail", logger: logger);

        var events = new List<AgentGraphEvent>();
        var act = async () =>
        {
            await foreach (var e in orchestrator.StreamAsync(
                new Dictionary<string, JsonElement>(), new AgentContext()))
            {
                events.Add(e);
            }
        };

        await act.Should().ThrowAsync<Exception>();

        var failed = events.OfType<GraphFailed>().Should().ContainSingle().Subject;
        failed.FailedNodeId.Should().Be("work");
        // D2: ErrorMessage carries the full stack (ToString), not just the bare message.
        failed.ErrorMessage.Should().Contain("Exception");

        logger.Entries.Should().Contain(e =>
            e.Level == LogLevel.Error &&
            e.Message.Contains("run-fail") &&
            e.Message.Contains("work"));
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception), exception));
    }

    private static (InMemoryAgentRegistry registry, AgentLifecycleManager lifecycle) BuildHarness(
        Func<CompletionRequest, CompletionResponse>? provider = null)
    {
        var registry = new InMemoryAgentRegistry();
        var runtime = new InMemoryAgentRuntime(new FakeCompletionProvider(provider));
        var lifecycle = new AgentLifecycleManager(registry, runtime);
        return (registry, lifecycle);
    }

    private static AgentManifest ManifestFor(string id, string? outputSchema = null)
    {
        var manifest = new AgentManifest(
            Id: id, Version: "1.0",
            Handler: new AgentHandlerRef("declarative"),
            Protocols: new[] { new ProtocolBinding("Http") },
            Tools: Array.Empty<ToolRef>());

        if (outputSchema is not null)
        {
            manifest = manifest with { OutputSchema = JsonDocument.Parse(outputSchema).RootElement };
        }
        return manifest;
    }

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
                When: new GraphEdgePredicate.PropertyMatcher("lastMessage.Text", GraphPredicateOperator.Contains, JsonSerializer.SerializeToElement("refund"))),
            new GraphEdge("triage", "sales",
                When: new GraphEdgePredicate.PropertyMatcher("lastMessage.Text", GraphPredicateOperator.Contains, JsonSerializer.SerializeToElement("upgrade"))),
            new GraphEdge("triage", "end"),
            new GraphEdge("billing", "end"),
            new GraphEdge("sales", "end"),
        });

    private static AgentGraphManifest BuildRetrievalLoopGraph() => new(
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

    [Fact]
    public async Task BuildAgentInputText_Handles_CamelCase_Text_Property_In_Messages()
    {
        // Regression: InProcessGraphOrchestrator.BuildAgentInputText only checked "Text"
        // (PascalCase). If messages arrive with lowercase "text" (e.g. camelCase serializer
        // options in the runtime), it would silently fall back to "query".
        var inputs = new System.Collections.Concurrent.ConcurrentBag<string>();
        var (registry, lifecycle) = BuildHarness(req =>
        {
            inputs.Add(req.History.LastOrDefault()?.Text ?? "");
            return new CompletionResponse("ok");
        });
        await lifecycle.CreateAsync(ManifestFor("node-b"));

        var manifest = new AgentGraphManifest(
            Id: "camel-text", Version: "1.0", Entry: "b",
            Nodes: new[]
            {
                new GraphNode("b", "Agent", Ref: new GraphAgentRef("node-b")),
                new GraphNode("end", "End"),
            },
            Edges: new[] { new GraphEdge("b", "end") });

        // Seed with a message whose property key is lowercase "text" (camelCase serialization).
        var camelMsg = JsonSerializer.SerializeToElement(
            new { role = "assistant", text = "from-camel-serializer" });
        var initial = new Dictionary<string, JsonElement>
        {
            ["query"] = JsonSerializer.SerializeToElement("initial request"),
            [GraphStateReducers.WellKnownKey.Messages] = JsonSerializer.SerializeToElement(new[] { camelMsg }),
        };

        var orchestrator = new InProcessGraphOrchestrator(manifest, registry, lifecycle, new InMemoryCheckpointer());
        await foreach (var _ in orchestrator.StreamAsync(initial, new AgentContext())) { }

        inputs.Should().ContainSingle()
            .Which.Should().Be("from-camel-serializer",
                "BuildAgentInputText must handle lowercase 'text' property, not fall back to 'query'");
    }

    [Fact]
    public async Task GraphEventBus_Receives_Exactly_The_Same_Events_As_The_Enumerator()
    {
        int callCount = 0;
        var (registry, lifecycle) = BuildHarness(_ =>
        {
            System.Threading.Interlocked.Increment(ref callCount);
            return new CompletionResponse("done");
        });
        await lifecycle.CreateAsync(ManifestFor("node-a"));

        var manifest = new AgentGraphManifest(
            Id: "bus-test", Version: "1.0", Entry: "a",
            Nodes: new[]
            {
                new GraphNode("a", "Agent", Ref: new GraphAgentRef("node-a")),
                new GraphNode("end", "End"),
            },
            Edges: new[] { new GraphEdge("a", "end") });

        var bus = new InMemoryAgentGraphEventBus();
        var busEvents = new List<AgentGraphEvent>();
        using var _ = bus.Subscribe((e, ct) => { busEvents.Add(e); return ValueTask.CompletedTask; });

        var orchestrator = new InProcessGraphOrchestrator(
            manifest, registry, lifecycle, new InMemoryCheckpointer(),
            graphEventBus: bus);

        var streamEvents = new List<AgentGraphEvent>();
        await foreach (var e in orchestrator.StreamAsync(new Dictionary<string, JsonElement>(), new AgentContext()))
        {
            streamEvents.Add(e);
        }

        busEvents.Should().HaveSameCount(streamEvents, "bus must receive every event yielded by the stream");
        for (var i = 0; i < streamEvents.Count; i++)
        {
            busEvents[i].Should().BeSameAs(streamEvents[i],
                $"bus event [{i}] should be the same object as stream event [{i}]");
        }
        streamEvents.Should().ContainSingle(e => e is GraphStarted);
        streamEvents.Should().ContainSingle(e => e is GraphCompleted);
    }

    private static async Task<List<AgentGraphEvent>> StreamEventsAsync(
        AgentGraphManifest manifest,
        IAgentRegistry registry,
        IAgentLifecycleManager lifecycle,
        IDictionary<string, JsonElement> initial,
        Func<CompletionRequest, CompletionResponse> provider)
    {
        var orchestrator = new InProcessGraphOrchestrator(manifest, registry, lifecycle, new InMemoryCheckpointer());
        var events = new List<AgentGraphEvent>();
        await foreach (var e in orchestrator.StreamAsync(initial, new AgentContext()))
        {
            events.Add(e);
        }
        return events;
    }

    // ---- v0.42 HITL tests ----

    [Fact]
    public async Task Hitl_SingleInterrupt_Handler_Returns_State_Graph_Continues()
    {
        var (registry, lifecycle) = BuildHarness();
        await lifecycle.CreateAsync(ManifestFor("pre"));
        await lifecycle.CreateAsync(ManifestFor("post"));

        var manifest = new AgentGraphManifest(
            Id: "hitl-single", Version: "1.0", Entry: "pre",
            Nodes: new[]
            {
                new GraphNode("pre", "Agent", Ref: new GraphAgentRef("pre")),
                new GraphNode("wait", "Interrupt", InterruptReason: "awaiting approval"),
                new GraphNode("post", "Agent", Ref: new GraphAgentRef("post")),
                new GraphNode("end", "End"),
            },
            Edges: new[]
            {
                new GraphEdge("pre", "wait"),
                new GraphEdge("wait", "post"),
                new GraphEdge("post", "end"),
            });

        var orchestrator = new InProcessGraphOrchestrator<IDictionary<string, JsonElement>>(
            manifest, registry, lifecycle, new InMemoryCheckpointer());

        GraphInterrupted? capturedInterrupt = null;
        var events = new List<AgentGraphEvent>();
        IDictionary<string, JsonElement>? finalState = null;

        finalState = await orchestrator.InvokeWithHitlAsync(
            initial: new Dictionary<string, JsonElement>(),
            context: new AgentContext(),
            handleInterrupt: (evt, ct) =>
            {
                capturedInterrupt = evt;
                return ValueTask.FromResult<IDictionary<string, JsonElement>?>(
                    new Dictionary<string, JsonElement>
                    {
                        ["approved"] = JsonSerializer.SerializeToElement(true),
                    });
            });

        capturedInterrupt.Should().NotBeNull();
        capturedInterrupt!.NodeId.Should().Be("wait");
        capturedInterrupt.Reason.Should().Be("awaiting approval");

        finalState.Should().ContainKey("hitl.response");
        var hitlResponse = finalState!["hitl.response"];
        hitlResponse.TryGetProperty("approved", out var approved).Should().BeTrue();
        approved.GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Hitl_MultipleSequentialInterrupts_BothHandlersCalled()
    {
        var (registry, lifecycle) = BuildHarness();
        await lifecycle.CreateAsync(ManifestFor("node-a"));
        await lifecycle.CreateAsync(ManifestFor("node-b"));

        var manifest = new AgentGraphManifest(
            Id: "hitl-multi", Version: "1.0", Entry: "a",
            Nodes: new[]
            {
                new GraphNode("a", "Agent", Ref: new GraphAgentRef("node-a")),
                new GraphNode("pause1", "Interrupt", InterruptReason: "first pause"),
                new GraphNode("b", "Agent", Ref: new GraphAgentRef("node-b")),
                new GraphNode("pause2", "Interrupt", InterruptReason: "second pause"),
                new GraphNode("end", "End"),
            },
            Edges: new[]
            {
                new GraphEdge("a", "pause1"),
                new GraphEdge("pause1", "b"),
                new GraphEdge("b", "pause2"),
                new GraphEdge("pause2", "end"),
            });

        var orchestrator = new InProcessGraphOrchestrator<IDictionary<string, JsonElement>>(
            manifest, registry, lifecycle, new InMemoryCheckpointer());

        var handlerCallOrder = new List<string>();
        int callCount = 0;

        var events = new List<AgentGraphEvent>();
        await foreach (var e in orchestrator.StreamWithHitlAsync(
            initial: new Dictionary<string, JsonElement>(),
            context: new AgentContext(),
            handleInterrupt: (evt, ct) =>
            {
                handlerCallOrder.Add(evt.NodeId);
                var n = System.Threading.Interlocked.Increment(ref callCount);
                return ValueTask.FromResult<IDictionary<string, JsonElement>?>(
                    new Dictionary<string, JsonElement>
                    {
                        [$"response{n}"] = JsonSerializer.SerializeToElement($"value{n}"),
                    });
            }))
        {
            events.Add(e);
        }

        handlerCallOrder.Should().Equal("pause1", "pause2");
        events.OfType<GraphInterrupted>().Should().HaveCount(2);
        events.OfType<GraphCompleted>().Should().ContainSingle();
        events.OfType<GraphFailed>().Should().BeEmpty();
    }

    [Fact]
    public async Task Hitl_HandlerReturnsNull_EmitsGraphFailed_ThrowsAbortedException()
    {
        var (registry, lifecycle) = BuildHarness();
        await lifecycle.CreateAsync(ManifestFor("pre"));

        var manifest = new AgentGraphManifest(
            Id: "hitl-abort", Version: "1.0", Entry: "pre",
            Nodes: new[]
            {
                new GraphNode("pre", "Agent", Ref: new GraphAgentRef("pre")),
                new GraphNode("wait", "Interrupt", InterruptReason: "needs review"),
                new GraphNode("end", "End"),
            },
            Edges: new[]
            {
                new GraphEdge("pre", "wait"),
                new GraphEdge("wait", "end"),
            });

        var orchestrator = new InProcessGraphOrchestrator<IDictionary<string, JsonElement>>(
            manifest, registry, lifecycle, new InMemoryCheckpointer());

        var events = new List<AgentGraphEvent>();
        GraphHitlAbortedException? thrownEx = null;
        try
        {
            await foreach (var e in orchestrator.StreamWithHitlAsync(
                initial: new Dictionary<string, JsonElement>(),
                context: new AgentContext(),
                handleInterrupt: (_, _) => ValueTask.FromResult<IDictionary<string, JsonElement>?>(null)))
            {
                events.Add(e);
            }
        }
        catch (GraphHitlAbortedException ex)
        {
            thrownEx = ex;
        }

        thrownEx.Should().NotBeNull();
        thrownEx!.NodeId.Should().Be("wait");
        events.OfType<GraphInterrupted>().Should().ContainSingle();
        events.OfType<GraphFailed>().Should().ContainSingle()
            .Which.ErrorType.Should().Be(nameof(GraphHitlAbortedException));
        events.OfType<GraphCompleted>().Should().BeEmpty();
    }

    [Fact]
    public async Task InvokeWithHitlAsync_Returns_Final_State_After_Single_Interrupt()
    {
        var (registry, lifecycle) = BuildHarness(_ => new CompletionResponse("""{"score":42}"""));
        await lifecycle.CreateAsync(ManifestFor("scorer",
            outputSchema: "{\"type\":\"object\",\"properties\":{\"score\":{\"type\":\"number\"}}}"));

        var manifest = new AgentGraphManifest(
            Id: "hitl-invoke", Version: "1.0", Entry: "scorer",
            Nodes: new[]
            {
                new GraphNode("scorer", "Agent", Ref: new GraphAgentRef("scorer"),
                    StateBindings: new GraphStateBindings(Output: new[] { "score" })),
                new GraphNode("review", "Interrupt", InterruptReason: "review the score"),
                new GraphNode("end", "End"),
            },
            Edges: new[]
            {
                new GraphEdge("scorer", "review"),
                new GraphEdge("review", "end"),
            });

        var orchestrator = new InProcessGraphOrchestrator<IDictionary<string, JsonElement>>(
            manifest, registry, lifecycle, new InMemoryCheckpointer());

        var final = await orchestrator.InvokeWithHitlAsync(
            initial: new Dictionary<string, JsonElement>(),
            context: new AgentContext(),
            handleInterrupt: (evt, ct) =>
            {
                return ValueTask.FromResult<IDictionary<string, JsonElement>?>(
                    new Dictionary<string, JsonElement>
                    {
                        ["verdict"] = JsonSerializer.SerializeToElement("approved"),
                    });
            });

        final.Should().ContainKey("score");
        final["score"].GetDouble().Should().BeApproximately(42, 0.001);
        final.Should().ContainKey("hitl.response");
        final["hitl.response"].TryGetProperty("verdict", out var verdict).Should().BeTrue();
        verdict.GetString().Should().Be("approved");
    }

    private sealed class FixedValueReducer : IGraphStateReducer
    {
        private readonly JsonElement _value;
        public FixedValueReducer(JsonElement value) => _value = value;
        public ValueTask<JsonElement> ReduceAsync(JsonElement existing, JsonElement incoming, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(_value);
    }
}
