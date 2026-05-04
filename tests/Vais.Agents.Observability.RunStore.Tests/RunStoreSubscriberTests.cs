// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Vais.Agents;
using Vais.Agents.Hosting.InMemory;
using Vais.Agents.Observability.RunStore;
using Xunit;

namespace Vais.Agents.Observability.RunStore.Tests;

public sealed class RunStoreSubscriberTests
{
    private static readonly AgentContext Ctx = new("test-user");

    // ---- in-process store ----

    private sealed class InMemoryRunStore : IRunStore
    {
        public readonly ConcurrentBag<(string Method, string RunId, string? Extra)> Calls = [];

        public Task InitializeAsync(CancellationToken ct = default) { Calls.Add(("init", "", null)); return Task.CompletedTask; }
        public Task StartRunAsync(string runId, string graphId, CancellationToken ct = default) { Calls.Add(("start-run", runId, graphId)); return Task.CompletedTask; }
        public Task CompleteRunAsync(string runId, int superSteps, CancellationToken ct = default) { Calls.Add(("complete-run", runId, superSteps.ToString())); return Task.CompletedTask; }
        public Task FailRunAsync(string runId, string error, CancellationToken ct = default) { Calls.Add(("fail-run", runId, error)); return Task.CompletedTask; }
        public Task InterruptRunAsync(string runId, string interruptId, CancellationToken ct = default) { Calls.Add(("interrupt-run", runId, interruptId)); return Task.CompletedTask; }
        public Task StartNodeAsync(string runId, string nodeId, string nodeKind, string? agentId, CancellationToken ct = default) { Calls.Add(("start-node", runId, nodeId)); return Task.CompletedTask; }
        public Task CompleteNodeAsync(string runId, string nodeId, CancellationToken ct = default) { Calls.Add(("complete-node", runId, nodeId)); return Task.CompletedTask; }
        public Task RecordNodeInvocationAsync(string runId, string nodeId, string agentId, string inputText, string outputText, int inputTokens, int outputTokens, CancellationToken ct = default) { Calls.Add(("record-invocation", runId, nodeId)); return Task.CompletedTask; }
        public Task RecordEdgeAsync(string runId, string fromNodeId, string toNodeId, CancellationToken ct = default) { Calls.Add(("record-edge", runId, $"{fromNodeId}->{toNodeId}")); return Task.CompletedTask; }
        public Task DeleteRunsOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct = default) { Calls.Add(("prune", "", null)); return Task.CompletedTask; }
        public Task<IReadOnlyList<PipelineRun>> ListRunsAsync(string graphId, RunStatus? status = null, DateTimeOffset? since = null, DateTimeOffset? until = null, int limit = 20, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<PipelineRun>>([]);
        public Task<PipelineRun?> GetRunAsync(string runId, CancellationToken ct = default) => Task.FromResult<PipelineRun?>(null);
        public Task<IReadOnlyList<NodeExecution>> GetNodesAsync(string runId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<NodeExecution>>([]);
        public Task<NodeExecution?> GetNodeAsync(string runId, string nodeId, CancellationToken ct = default) => Task.FromResult<NodeExecution?>(null);
    }

    private static (RunStoreSubscriber Subscriber, InMemoryRunStore Store, InMemoryAgentGraphEventBus Bus) BuildSut(int retentionDays = 30)
    {
        var store = new InMemoryRunStore();
        var bus = new InMemoryAgentGraphEventBus(NullLogger<InMemoryAgentGraphEventBus>.Instance);
        var opts = Options.Create(new RunStoreOptions { RetentionDays = retentionDays });
        var logger = NullLogger<RunStoreSubscriber>.Instance;
        var subscriber = new RunStoreSubscriber(store, bus, opts, logger);
        return (subscriber, store, bus);
    }

    [Fact]
    public async Task StartAsync_Calls_InitializeAndPrune()
    {
        var (sub, store, _) = BuildSut();

        await sub.StartAsync(CancellationToken.None);
        await sub.StopAsync(CancellationToken.None);

        store.Calls.Should().Contain(c => c.Method == "init");
        store.Calls.Should().Contain(c => c.Method == "prune");
    }

    [Fact]
    public async Task GraphStarted_Event_Calls_StartRun()
    {
        var (sub, store, bus) = BuildSut();
        await sub.StartAsync(CancellationToken.None);

        await bus.PublishAsync(new GraphStarted(DateTimeOffset.UtcNow, Ctx, "run-1", 0, "my-graph", "v1", "entry"));

        store.Calls.Should().Contain(c => c.Method == "start-run" && c.RunId == "run-1" && c.Extra == "my-graph");
    }

    [Fact]
    public async Task NodeStarted_Event_Calls_StartNode()
    {
        var (sub, store, bus) = BuildSut();
        await sub.StartAsync(CancellationToken.None);

        await bus.PublishAsync(new NodeStarted(DateTimeOffset.UtcNow, Ctx, "run-1", 0, "node-a", "agent"));

        store.Calls.Should().Contain(c => c.Method == "start-node" && c.RunId == "run-1" && c.Extra == "node-a");
    }

    [Fact]
    public async Task NodeAgentInvoked_Event_Calls_RecordNodeInvocation()
    {
        var (sub, store, bus) = BuildSut();
        await sub.StartAsync(CancellationToken.None);

        await bus.PublishAsync(new NodeAgentInvoked(DateTimeOffset.UtcNow, Ctx, "run-1", 0,
            "node-a", "agent-x", "hello", "world", 10, 20));

        store.Calls.Should().Contain(c => c.Method == "record-invocation" && c.RunId == "run-1" && c.Extra == "node-a");
    }

    [Fact]
    public async Task NodeCompleted_Event_Calls_CompleteNode()
    {
        var (sub, store, bus) = BuildSut();
        await sub.StartAsync(CancellationToken.None);

        await bus.PublishAsync(new NodeCompleted(DateTimeOffset.UtcNow, Ctx, "run-1", 0, "node-a", "agent", TimeSpan.FromMilliseconds(50)));

        store.Calls.Should().Contain(c => c.Method == "complete-node" && c.RunId == "run-1" && c.Extra == "node-a");
    }

    [Fact]
    public async Task EdgeTraversed_Event_Calls_RecordEdge()
    {
        var (sub, store, bus) = BuildSut();
        await sub.StartAsync(CancellationToken.None);

        await bus.PublishAsync(new EdgeTraversed(DateTimeOffset.UtcNow, Ctx, "run-1", 0, "node-a", "node-b"));

        store.Calls.Should().Contain(c => c.Method == "record-edge" && c.RunId == "run-1" && c.Extra == "node-a->node-b");
    }

    [Fact]
    public async Task GraphCompleted_Event_Calls_CompleteRun()
    {
        var (sub, store, bus) = BuildSut();
        await sub.StartAsync(CancellationToken.None);

        await bus.PublishAsync(new GraphCompleted(DateTimeOffset.UtcNow, Ctx, "run-1", 3, "end", TimeSpan.FromSeconds(1)));

        store.Calls.Should().Contain(c => c.Method == "complete-run" && c.RunId == "run-1" && c.Extra == "3");
    }

    [Fact]
    public async Task GraphFailed_Event_Calls_FailRun()
    {
        var (sub, store, bus) = BuildSut();
        await sub.StartAsync(CancellationToken.None);

        await bus.PublishAsync(new GraphFailed(DateTimeOffset.UtcNow, Ctx, "run-1", 1, "InvalidOperationException", "boom", TimeSpan.FromSeconds(2)));

        store.Calls.Should().Contain(c => c.Method == "fail-run" && c.RunId == "run-1" && c.Extra == "boom");
    }

    [Fact]
    public async Task GraphInterrupted_Event_Calls_InterruptRun()
    {
        var (sub, store, bus) = BuildSut();
        await sub.StartAsync(CancellationToken.None);

        await bus.PublishAsync(new GraphInterrupted(DateTimeOffset.UtcNow, Ctx, "run-1", 1, "node-a", "interrupt-42", null));

        store.Calls.Should().Contain(c => c.Method == "interrupt-run" && c.RunId == "run-1" && c.Extra == "interrupt-42");
    }

    [Fact]
    public async Task StopAsync_Unsubscribes_From_Bus()
    {
        var (sub, store, bus) = BuildSut();
        await sub.StartAsync(CancellationToken.None);
        await sub.StopAsync(CancellationToken.None);

        var countBefore = store.Calls.Count;
        await bus.PublishAsync(new GraphStarted(DateTimeOffset.UtcNow, Ctx, "run-2", 0, "g", "v1", "e"));

        store.Calls.Should().HaveCount(countBefore);
    }
}
