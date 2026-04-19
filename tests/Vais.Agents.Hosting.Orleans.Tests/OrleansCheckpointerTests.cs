// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using FluentAssertions;
using Vais.Agents.Control;
using Vais.Agents.Control.InProcess;
using Vais.Agents.Core;
using Vais.Agents.Hosting.InMemory;
using Xunit;

namespace Vais.Agents.Hosting.Orleans.Tests;

/// <summary>
/// v0.9 PR 4: Orleans-backed graph checkpointer. Covers the round-trip pattern
/// (save → get), the survives-silo-restart invariant, the full
/// interrupt→resume roundtrip via <see cref="IResumableAgentGraph{TState}"/>,
/// checkpoint retention on graph completion, and unknown-runId-returns-null.
/// </summary>
[Collection(OrleansClusterCollection.CollectionName)]
public sealed class OrleansCheckpointerTests
{
    private readonly OrleansClusterFixture _fixture;

    public OrleansCheckpointerTests(OrleansClusterFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task SaveCheckpoint_Then_Load_Round_Trips_Interrupt_State()
    {
        var store = new OrleansCheckpointer(_fixture.Cluster.Client);
        var runId = $"run-{Guid.NewGuid():N}";
        var original = new GraphCheckpoint(
            RunId: runId,
            GraphId: "test-graph",
            GraphVersion: "1.0",
            State: new Dictionary<string, JsonElement>
            {
                ["query"] = JsonSerializer.SerializeToElement("what is the best plan?"),
                ["retryCount"] = JsonSerializer.SerializeToElement(2),
            },
            NextNodeId: "wait-for-approval",
            SuperStepIndex: 5,
            PendingInterruptId: "int-abc",
            IsComplete: false,
            CreatedAt: DateTimeOffset.UtcNow);

        await store.SaveAsync(original);
        var loaded = await store.LoadAsync(runId);

        loaded.Should().NotBeNull();
        loaded!.RunId.Should().Be(runId);
        loaded.GraphId.Should().Be("test-graph");
        loaded.NextNodeId.Should().Be("wait-for-approval");
        loaded.PendingInterruptId.Should().Be("int-abc");
        loaded.SuperStepIndex.Should().Be(5);
        loaded.State["retryCount"].GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task Load_Unknown_RunId_Returns_Null()
    {
        var store = new OrleansCheckpointer(_fixture.Cluster.Client);

        var loaded = await store.LoadAsync($"ghost-{Guid.NewGuid():N}");

        loaded.Should().BeNull();
    }

    [Fact]
    public async Task Checkpoint_Survives_Simulated_Silo_Restart()
    {
        // Same pattern as v0.8's A2A task-store test: memory grain storage holds
        // IPersistentState across grain deactivations (within the test process).
        // ForceActivationCollection evicts the live grain; on next call the grain
        // re-activates and reads its state from the in-memory store.
        var store = new OrleansCheckpointer(_fixture.Cluster.Client);
        var runId = $"run-restart-{Guid.NewGuid():N}";
        await store.SaveAsync(new GraphCheckpoint(
            RunId: runId,
            GraphId: "durable-graph", GraphVersion: "1.0",
            State: new Dictionary<string, JsonElement> { ["hops"] = JsonSerializer.SerializeToElement(3) },
            NextNodeId: "wait",
            SuperStepIndex: 7,
            PendingInterruptId: "int-99",
            IsComplete: false,
            CreatedAt: DateTimeOffset.UtcNow));

        var management = _fixture.Cluster.Client.GetGrain<global::Orleans.Runtime.IManagementGrain>(0);
        await management.ForceActivationCollection(TimeSpan.Zero);

        var reloaded = await store.LoadAsync(runId);
        reloaded.Should().NotBeNull();
        reloaded!.NextNodeId.Should().Be("wait");
        reloaded.PendingInterruptId.Should().Be("int-99");
        reloaded.State["hops"].GetInt32().Should().Be(3);
    }

    [Fact]
    public async Task Full_Resume_Roundtrip_Continues_Graph_Past_Interrupt()
    {
        // Build a graph: start → interrupt → end. Run it once, verify the interrupt
        // fires + a checkpoint lands in the Orleans store. Then spin up a fresh
        // orchestrator from the same manifest, call ResumeAsync with the loaded
        // checkpoint + a resume payload, verify the graph completes.
        var registry = new InMemoryAgentRegistry();
        var runtime = new InMemoryAgentRuntime(new FakeProvider());
        var lifecycle = new AgentLifecycleManager(registry, runtime);
        await lifecycle.CreateAsync(ManifestFor("start"));

        var manifest = new AgentGraphManifest(
            Id: "resumable", Version: "1.0", Entry: "s",
            Nodes: new[]
            {
                new GraphNode("s", "Agent", Ref: new GraphAgentRef("start")),
                new GraphNode("pause", "Interrupt", InterruptReason: "needs approval"),
                new GraphNode("end", "End"),
            },
            Edges: new[]
            {
                new GraphEdge("s", "pause"),
                new GraphEdge("pause", "end"),
            });

        var checkpointer = new OrleansCheckpointer(_fixture.Cluster.Client);
        var runId = $"resume-{Guid.NewGuid():N}";
        var orchestrator1 = new InProcessGraphOrchestrator(
            manifest, registry, lifecycle, checkpointer,
            runIdFactory: () => runId);

        // First call: run until interrupt.
        var events1 = new List<AgentGraphEvent>();
        await foreach (var e in orchestrator1.StreamAsync(
            new Dictionary<string, JsonElement>(), new AgentContext(UserId: "u1")))
        {
            events1.Add(e);
        }
        events1.OfType<GraphInterrupted>().Should().ContainSingle();

        // Load the checkpoint from Orleans + resume on a fresh orchestrator.
        var loaded = await checkpointer.LoadAsync(runId);
        loaded.Should().NotBeNull();
        loaded!.NextNodeId.Should().Be("pause");
        loaded.IsComplete.Should().BeFalse();

        var orchestrator2 = new InProcessGraphOrchestrator(
            manifest, registry, lifecycle, checkpointer,
            runIdFactory: () => runId);
        var resumeEvents = new List<AgentGraphEvent>();
        await foreach (var e in orchestrator2.ResumeStreamAsync(
            loaded, resumePayload: null, new AgentContext(UserId: "u1")))
        {
            resumeEvents.Add(e);
        }

        resumeEvents.OfType<GraphResumed>().Should().ContainSingle();
        resumeEvents.OfType<GraphCompleted>().Should().ContainSingle();
        resumeEvents.OfType<GraphInterrupted>().Should().BeEmpty(); // doesn't re-fire

        // Final checkpoint is the complete one.
        var final = await checkpointer.LoadAsync(runId);
        final.Should().NotBeNull();
        final!.IsComplete.Should().BeTrue();
    }

    [Fact]
    public async Task Completed_Checkpoint_Is_Retained_Until_Explicit_Delete()
    {
        // Retention contract: completed checkpoints stay in the store until the
        // caller explicitly deletes them (useful for audit + "has this run been
        // processed?" queries). Tests the Delete verb too.
        var store = new OrleansCheckpointer(_fixture.Cluster.Client);
        var runId = $"retain-{Guid.NewGuid():N}";
        await store.SaveAsync(new GraphCheckpoint(
            RunId: runId,
            GraphId: "g", GraphVersion: "1.0",
            State: new Dictionary<string, JsonElement>(),
            NextNodeId: null, SuperStepIndex: 3, PendingInterruptId: null,
            IsComplete: true,
            CreatedAt: DateTimeOffset.UtcNow));

        var retained = await store.LoadAsync(runId);
        retained.Should().NotBeNull();
        retained!.IsComplete.Should().BeTrue();

        await store.DeleteAsync(runId);
        (await store.LoadAsync(runId)).Should().BeNull();
    }

    // ---- helpers ----

    private static AgentManifest ManifestFor(string id) => new(
        Id: id, Version: "1.0",
        Handler: new AgentHandlerRef("declarative"),
        Protocols: new[] { new ProtocolBinding("Http") },
        Tools: Array.Empty<ToolRef>());

    private sealed class FakeProvider : ICompletionProvider
    {
        public string ProviderName => "Fake";
        public Task<CompletionResponse> CompleteAsync(CompletionRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new CompletionResponse("ok"));
    }
}
