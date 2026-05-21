// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Vais.Agents.Hosting.Orleans.Tests;

/// <summary>
/// End-to-end round-trip tests for <see cref="OrleansAgentGraphEventBus"/> against Orleans'
/// in-process <c>AddMemoryStreams</c> (reusing <see cref="OrleansStreamsFixture"/>). Verifies the
/// surrogate + observer adapter + IDisposable wiring for a representative spread of
/// <see cref="AgentGraphEvent"/> subtypes; exhaustive per-subtype field coverage lives in
/// <see cref="AgentGraphEventSurrogateTests"/>. The graph bus shares the fixture's stream provider
/// (<see cref="OrleansAgentEventBus.StreamNamespace"/>) but uses its own stream namespace.
/// </summary>
[Collection(OrleansStreamsCollection.CollectionName)]
public sealed class OrleansAgentGraphEventBusTests
{
    private readonly OrleansStreamsFixture _fx;

    public OrleansAgentGraphEventBusTests(OrleansStreamsFixture fx) => _fx = fx;

    private OrleansAgentGraphEventBus NewBus()
        => new(_fx.Cluster.Client, OrleansAgentEventBus.StreamNamespace);

    private static readonly AgentContext Ctx = new(UserId: "u", AgentName: "a") { RunId = "run-1" };

    [Fact]
    public async Task Publishes_And_Subscribes_GraphStarted_Round_Trip()
    {
        var bus = NewBus();
        GraphStarted? observed = null;
        var tcs = new TaskCompletionSource();
        using var sub = bus.Subscribe((e, _) =>
        {
            if (e is GraphStarted gs) { observed = gs; tcs.TrySetResult(); }
            return ValueTask.CompletedTask;
        });

        await bus.PublishAsync(new GraphStarted(DateTimeOffset.UtcNow, Ctx, "run-1", 0, "graph-1", "1.0", "entry"));

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        observed.Should().NotBeNull();
        observed!.GraphId.Should().Be("graph-1");
        observed.EntryNodeId.Should().Be("entry");
        observed.Context.AgentName.Should().Be("a");
    }

    [Fact]
    public async Task Publishes_And_Subscribes_GraphFailed_With_FailedNodeId_Round_Trip()
    {
        var bus = NewBus();
        GraphFailed? observed = null;
        var tcs = new TaskCompletionSource();
        using var sub = bus.Subscribe((e, _) =>
        {
            if (e is GraphFailed gf) { observed = gf; tcs.TrySetResult(); }
            return ValueTask.CompletedTask;
        });

        await bus.PublishAsync(new GraphFailed(
            DateTimeOffset.UtcNow, Ctx, "run-1", 4, "InvalidOperationException", "boom",
            TimeSpan.FromMilliseconds(9), FailedNodeId: "node-7"));

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        observed.Should().NotBeNull();
        observed!.ErrorType.Should().Be("InvalidOperationException");
        observed.FailedNodeId.Should().Be("node-7");
    }

    [Fact]
    public async Task Publishes_And_Subscribes_GraphCompleted_With_FinalState_Round_Trip()
    {
        var bus = NewBus();
        GraphCompleted? observed = null;
        var tcs = new TaskCompletionSource();
        using var sub = bus.Subscribe((e, _) =>
        {
            if (e is GraphCompleted gc) { observed = gc; tcs.TrySetResult(); }
            return ValueTask.CompletedTask;
        });

        var finalState = new Dictionary<string, JsonElement>
        {
            ["answer"] = JsonSerializer.SerializeToElement(42),
        };
        await bus.PublishAsync(new GraphCompleted(
            DateTimeOffset.UtcNow, Ctx, "run-1", 8, "end", TimeSpan.FromSeconds(1), finalState));

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        observed.Should().NotBeNull();
        observed!.FinalNodeId.Should().Be("end");
        observed.FinalState.Should().NotBeNull();
        observed.FinalState!["answer"].GetInt32().Should().Be(42);
    }

    [Fact]
    public async Task Publishes_And_Subscribes_StateUpdated_Round_Trip()
    {
        var bus = NewBus();
        StateUpdated? observed = null;
        var tcs = new TaskCompletionSource();
        using var sub = bus.Subscribe((e, _) =>
        {
            if (e is StateUpdated su) { observed = su; tcs.TrySetResult(); }
            return ValueTask.CompletedTask;
        });

        await bus.PublishAsync(new StateUpdated(DateTimeOffset.UtcNow, Ctx, "run-1", 5, new[] { "k1", "k2" }));

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        observed.Should().NotBeNull();
        observed!.ChangedKeys.Should().Equal("k1", "k2");
    }

    [Fact]
    public async Task Dispose_Unsubscribes_And_Later_Events_Are_Not_Received()
    {
        var bus = NewBus();
        var firstReceived = new TaskCompletionSource();
        var count = 0;
        var sub = bus.Subscribe((_, _) =>
        {
            count++;
            firstReceived.TrySetResult();
            return ValueTask.CompletedTask;
        });

        await bus.PublishAsync(new GraphStarted(DateTimeOffset.UtcNow, Ctx, "run-1", 0, "g", "1.0", "entry"));
        await firstReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));

        sub.Dispose();
        await Task.Delay(100);

        await bus.PublishAsync(new GraphStarted(DateTimeOffset.UtcNow, Ctx, "run-1", 0, "g", "1.0", "entry"));
        await Task.Delay(300);

        count.Should().Be(1);
    }
}
