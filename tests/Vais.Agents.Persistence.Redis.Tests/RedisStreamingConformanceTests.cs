// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Vais.Agents.Hosting.Orleans;
using Xunit;

namespace Vais.Agents.Persistence.Redis.Tests;

/// <summary>
/// Cross-silo conformance for the event buses over a real Redis stream provider on a multi-silo
/// <see cref="Orleans.TestingHost.TestCluster"/> (research/cross-silo-gaps-2026-05-21.md §5 step 3).
/// Confirms that <see cref="OrleansAgentGraphEventBus"/> events round-trip over Redis and — the
/// open question OQ-1 — that the stream fans out to <b>every</b> subscriber rather than coalescing
/// to one. The fan-out result is why per-silo subscribers (e.g. <c>RunStoreSubscriber</c>) needed
/// idempotent writes (G3).
/// </summary>
/// <remarks>
/// Reuses <see cref="RedisStreamingFixture"/> (Redis container + cluster with
/// <c>UseAgenticRedisStreaming</c>). G4 cross-silo run-grain addressability is covered separately by
/// <c>AgentGraphRunGrainTests</c> (single-activation grain on a multi-silo cluster).
/// <c>Microsoft.Orleans.Streaming.Redis</c> is alpha (G2) — treat this as a canary too.
/// </remarks>
[Collection(RedisStreamingCollection.CollectionName)]
public sealed class RedisStreamingConformanceTests
{
    private readonly RedisStreamingFixture _fx;

    public RedisStreamingConformanceTests(RedisStreamingFixture fx) => _fx = fx;

    [Fact]
    public void Cluster_Is_Multi_Silo()
        => _fx.Cluster.Silos.Count.Should().BeGreaterThan(1, "cross-silo conformance needs more than one silo");

    [Fact]
    public async Task GraphEventBus_RoundTrips_GraphFailed_Over_Redis()
    {
        var bus = new OrleansAgentGraphEventBus(_fx.Cluster.Client, OrleansAgentEventBus.StreamNamespace);

        var tcs = new TaskCompletionSource<AgentGraphEvent>();
        using var sub = bus.Subscribe((e, _) =>
        {
            if (e is GraphFailed) tcs.TrySetResult(e);
            return ValueTask.CompletedTask;
        });

        await bus.PublishAsync(new GraphFailed(
            DateTimeOffset.UtcNow, AgentContext.Empty, "run-conformance-1", 3,
            "InvalidOperationException", "boom", TimeSpan.FromMilliseconds(5), FailedNodeId: "node-7"));

        var received = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(15));
        received.Should().BeOfType<GraphFailed>()
            .Which.FailedNodeId.Should().Be("node-7", "the surrogate must round-trip FailedNodeId over Redis");
    }

    [Fact]
    public async Task Two_Subscribers_Each_Receive_Every_Event()
    {
        // OQ-1: two independent subscriptions to the one graph-event stream — modelling a
        // RunStoreSubscriber on each silo. Both must receive the single published event (fan-out),
        // which is exactly why those subscribers must be idempotent (G3). If Orleans coalesced to a
        // single consumer, only one would fire and this would time out.
        var bus = new OrleansAgentGraphEventBus(_fx.Cluster.Client, OrleansAgentEventBus.StreamNamespace);

        var a = new TaskCompletionSource();
        var b = new TaskCompletionSource();
        using var subA = bus.Subscribe((e, _) =>
        {
            if (e is GraphStarted gs && gs.RunId == "run-conformance-2") a.TrySetResult();
            return ValueTask.CompletedTask;
        });
        using var subB = bus.Subscribe((e, _) =>
        {
            if (e is GraphStarted gs && gs.RunId == "run-conformance-2") b.TrySetResult();
            return ValueTask.CompletedTask;
        });

        await bus.PublishAsync(new GraphStarted(
            DateTimeOffset.UtcNow, AgentContext.Empty, "run-conformance-2", 0, "g", "1.0", "entry"));

        var bothReceived = Task.WhenAll(a.Task, b.Task);
        await bothReceived.WaitAsync(TimeSpan.FromSeconds(15));
        bothReceived.IsCompletedSuccessfully.Should().BeTrue(
            "Redis streams fan out to every explicit subscriber — confirming G3's per-silo duplicate-delivery hazard");
    }
}
