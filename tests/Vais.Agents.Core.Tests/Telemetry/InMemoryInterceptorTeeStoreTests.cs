// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Xunit;

namespace Vais.Agents.Core.Tests.Telemetry;

/// <summary>
/// D-2 verify gate: in-memory bounded ring store. Append + Query round-trip;
/// query filters (agent, run, concept, transport, since/until, outcome, limit) honored;
/// ring-buffer eviction policy correct; thread-safe under concurrent append.
/// </summary>
public sealed class InMemoryInterceptorTeeStoreTests
{
    [Fact]
    public async Task Append_ThenQuery_ReturnsNewestFirst()
    {
        var store = new InMemoryInterceptorTeeStore(capacity: 8);

        await store.AppendAsync(Evt("a", t: DateTimeOffset.Parse("2026-05-26T10:00Z")));
        await store.AppendAsync(Evt("b", t: DateTimeOffset.Parse("2026-05-26T10:01Z")));
        await store.AppendAsync(Evt("c", t: DateTimeOffset.Parse("2026-05-26T10:02Z")));

        var seen = await Collect(store.QueryAsync(new TrajectoryQuery()));

        seen.Select(e => e.EventId).Should().Equal("c", "b", "a");
        store.Count.Should().Be(3);
    }

    [Fact]
    public async Task RingBuffer_EvictsOldestOnceCapacityReached()
    {
        var store = new InMemoryInterceptorTeeStore(capacity: 3);

        for (var i = 0; i < 5; i++)
            await store.AppendAsync(Evt($"e{i}"));

        store.Count.Should().Be(3, "ring caps at capacity");
        var seen = await Collect(store.QueryAsync(new TrajectoryQuery()));
        seen.Select(e => e.EventId).Should().Equal("e4", "e3", "e2");
    }

    [Fact]
    public async Task Query_FiltersByAgentId()
    {
        var store = new InMemoryInterceptorTeeStore();
        await store.AppendAsync(Evt("a", agentId: "coord"));
        await store.AppendAsync(Evt("b", agentId: "worker"));
        await store.AppendAsync(Evt("c", agentId: "coord"));

        var seen = await Collect(store.QueryAsync(new TrajectoryQuery(AgentId: "coord")));

        seen.Select(e => e.EventId).Should().BeEquivalentTo(["c", "a"]);
    }

    [Fact]
    public async Task Query_FiltersByMultipleFieldsSimultaneously()
    {
        var store = new InMemoryInterceptorTeeStore();
        await store.AppendAsync(Evt("a", agentId: "coord", concept: "reviewer", transport: "south"));
        await store.AppendAsync(Evt("b", agentId: "coord", concept: "deployer", transport: "south"));
        await store.AppendAsync(Evt("c", agentId: "coord", concept: "reviewer", transport: "north"));

        var seen = await Collect(store.QueryAsync(new TrajectoryQuery(
            AgentId: "coord", ConceptName: "reviewer", Transport: "south")));

        seen.Select(e => e.EventId).Should().ContainSingle().Which.Should().Be("a");
    }

    [Fact]
    public async Task Query_FiltersBySinceAndUntilTimeRange()
    {
        var store = new InMemoryInterceptorTeeStore();
        await store.AppendAsync(Evt("old", t: DateTimeOffset.Parse("2026-05-26T08:00Z")));
        await store.AppendAsync(Evt("mid", t: DateTimeOffset.Parse("2026-05-26T10:00Z")));
        await store.AppendAsync(Evt("new", t: DateTimeOffset.Parse("2026-05-26T12:00Z")));

        var seen = await Collect(store.QueryAsync(new TrajectoryQuery(
            Since: DateTimeOffset.Parse("2026-05-26T09:00Z"),
            Until: DateTimeOffset.Parse("2026-05-26T11:00Z"))));

        seen.Select(e => e.EventId).Should().Equal("mid");
    }

    [Fact]
    public async Task Query_FiltersByOutcomeKind()
    {
        var store = new InMemoryInterceptorTeeStore();
        await store.AppendAsync(Evt("ok", outcome: new TrajectoryOutcome(TrajectoryOutcomeKind.Ok)));
        await store.AppendAsync(Evt("err", outcome: new TrajectoryOutcome(TrajectoryOutcomeKind.Error, "Boom")));
        await store.AppendAsync(Evt("sc", outcome: new TrajectoryOutcome(TrajectoryOutcomeKind.ShortCircuit)));

        var errors = await Collect(store.QueryAsync(new TrajectoryQuery(OutcomeKind: TrajectoryOutcomeKind.Error)));

        errors.Should().ContainSingle().Which.EventId.Should().Be("err");
    }

    [Fact]
    public async Task Query_HonorsLimit()
    {
        var store = new InMemoryInterceptorTeeStore();
        for (var i = 0; i < 10; i++) await store.AppendAsync(Evt($"e{i}"));

        var seen = await Collect(store.QueryAsync(new TrajectoryQuery(Limit: 3)));

        seen.Should().HaveCount(3);
    }

    [Fact]
    public async Task ConcurrentAppends_AllSurviveAndCountIsAccurate()
    {
        var store = new InMemoryInterceptorTeeStore(capacity: 1000);

        var tasks = Enumerable.Range(0, 100).Select(i =>
            Task.Run(async () =>
            {
                for (var j = 0; j < 10; j++)
                    await store.AppendAsync(Evt($"t{i}-e{j}"));
            })).ToArray();
        await Task.WhenAll(tasks);

        store.Count.Should().Be(1000);
        var all = await Collect(store.QueryAsync(new TrajectoryQuery()));
        all.Should().HaveCount(1000);
    }

    [Fact]
    public void Constructor_RejectsNonPositiveCapacity()
    {
        FluentActions.Invoking(() => new InMemoryInterceptorTeeStore(capacity: 0))
            .Should().Throw<ArgumentOutOfRangeException>();
        FluentActions.Invoking(() => new InMemoryInterceptorTeeStore(capacity: -1))
            .Should().Throw<ArgumentOutOfRangeException>();
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static TrajectoryEvent Evt(
        string id,
        DateTimeOffset? t = null,
        string? agentId = null,
        string? concept = null,
        string? transport = null,
        TrajectoryOutcome? outcome = null) =>
        new()
        {
            EventId = id,
            Timestamp = t ?? DateTimeOffset.UtcNow,
            EventName = "tool.call",
            Operation = OntologyOperation.Call,
            AgentId = agentId,
            ConceptName = concept,
            Transport = transport,
            Outcome = outcome,
        };

    private static async Task<List<TrajectoryEvent>> Collect(IAsyncEnumerable<TrajectoryEvent> src)
    {
        var list = new List<TrajectoryEvent>();
        await foreach (var e in src) list.Add(e);
        return list;
    }
}
