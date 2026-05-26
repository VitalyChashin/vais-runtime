// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.PostgreSql;
using Xunit;

namespace Vais.Agents.Observability.InterceptorTeeStore.Tests;

/// <summary>
/// D-4 integration tests for <see cref="PostgresInterceptorTeeStore"/>. Stands an ephemeral
/// <c>postgres:16-alpine</c> container, applies the schema, exercises append + query +
/// retention + filter combinations. Requires Docker.
/// </summary>
public sealed class PostgresInterceptorTeeStoreTests : IAsyncLifetime
{
    private PostgreSqlContainer _postgres = null!;
    private PostgresInterceptorTeeStore _store = null!;

    public async Task InitializeAsync()
    {
        _postgres = new PostgreSqlBuilder("postgres:16-alpine").Build();
        await _postgres.StartAsync();
        _store = new PostgresInterceptorTeeStore(_postgres.GetConnectionString(), NullLogger<PostgresInterceptorTeeStore>.Instance);
        await _store.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        await _postgres.DisposeAsync();
    }

    [Fact]
    public async Task Schema_IsIdempotent_InitializeAgainSucceeds()
    {
        // First InitializeAsync ran in test setup; calling again must not fail.
        await _store.InitializeAsync();
    }

    [Fact]
    public async Task Append_ThenQuery_RoundTripsAllFields()
    {
        var evt = NewEvent("e1", agentId: "coord", concept: "fetch_url", transport: "south",
            outcome: new TrajectoryOutcome(TrajectoryOutcomeKind.Ok),
            shape: new Dictionary<string, string> { ["url"] = "string", ["limit"] = "number" });

        await _store.AppendAsync(evt);
        var stored = await Single(_store.QueryAsync(new TrajectoryQuery(AgentId: "coord")));

        stored.EventId.Should().Be("e1");
        stored.AgentId.Should().Be("coord");
        stored.ConceptName.Should().Be("fetch_url");
        stored.Transport.Should().Be("south");
        stored.Operation.Should().Be(OntologyOperation.Call);
        stored.Outcome.Should().Be(new TrajectoryOutcome(TrajectoryOutcomeKind.Ok));
        stored.ArgumentsShape.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["url"] = "string",
            ["limit"] = "number",
        });
    }

    [Fact]
    public async Task Append_IsIdempotentOnEventId()
    {
        var evt = NewEvent("dup1", agentId: "x");

        await _store.AppendAsync(evt);
        await _store.AppendAsync(evt); // duplicate

        var all = await Collect(_store.QueryAsync(new TrajectoryQuery(AgentId: "x")));
        all.Should().ContainSingle("ON CONFLICT (event_id) DO NOTHING");
    }

    [Fact]
    public async Task Query_FiltersByAgentRunConceptTransportOutcomeAndTimeRange()
    {
        var t0 = DateTimeOffset.UtcNow;
        await _store.AppendAsync(NewEvent("a", agentId: "coord", run: "r1", concept: "fetch", transport: "south",
            outcome: new TrajectoryOutcome(TrajectoryOutcomeKind.Ok), timestamp: t0.AddMinutes(-10)));
        await _store.AppendAsync(NewEvent("b", agentId: "coord", run: "r1", concept: "fetch", transport: "south",
            outcome: new TrajectoryOutcome(TrajectoryOutcomeKind.Error, "Boom"), timestamp: t0.AddMinutes(-5)));
        await _store.AppendAsync(NewEvent("c", agentId: "other", timestamp: t0.AddMinutes(-1)));

        var errors = await Collect(_store.QueryAsync(new TrajectoryQuery(
            AgentId: "coord", RunId: "r1", ConceptName: "fetch", Transport: "south",
            OutcomeKind: TrajectoryOutcomeKind.Error,
            Since: t0.AddMinutes(-7), Until: t0)));

        errors.Should().ContainSingle().Which.EventId.Should().Be("b");
    }

    [Fact]
    public async Task Query_OrdersNewestFirst_HonorsLimit()
    {
        for (var i = 0; i < 5; i++)
            await _store.AppendAsync(NewEvent($"e{i}", agentId: "limited", timestamp: DateTimeOffset.UtcNow.AddSeconds(i)));

        var top3 = await Collect(_store.QueryAsync(new TrajectoryQuery(AgentId: "limited", Limit: 3)));

        top3.Should().HaveCount(3);
        top3.Select(e => e.EventId).Should().Equal("e4", "e3", "e2");
    }

    [Fact]
    public async Task DeleteEventsOlderThan_PrunesByCreatedAt()
    {
        var stale = NewEvent("old", agentId: "p");
        var fresh = NewEvent("new", agentId: "p");
        await _store.AppendAsync(stale);
        await _store.AppendAsync(fresh);

        // Hand-edit created_at on the "old" row to make it ancient.
        await using var conn = new Npgsql.NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "UPDATE vais_trajectory_events SET created_at = $1 WHERE event_id = 'old'";
            cmd.Parameters.AddWithValue(DateTimeOffset.UtcNow - TimeSpan.FromDays(90));
            await cmd.ExecuteNonQueryAsync();
        }

        await _store.DeleteEventsOlderThanAsync(DateTimeOffset.UtcNow - TimeSpan.FromDays(30));

        var remaining = await Collect(_store.QueryAsync(new TrajectoryQuery(AgentId: "p")));
        remaining.Should().ContainSingle().Which.EventId.Should().Be("new");
    }

    [Fact]
    public async Task NullableFields_RoundTripCorrectly()
    {
        var sparse = new TrajectoryEvent
        {
            EventId = "sparse",
            Timestamp = DateTimeOffset.UtcNow,
            EventName = "x",
            Operation = OntologyOperation.List,
            // AgentId, RunId, ConceptName, Transport, ArgumentsShape, Outcome, OntologyVersion, Duration all null
        };
        await _store.AppendAsync(sparse);
        var stored = await Single(_store.QueryAsync(new TrajectoryQuery(Limit: 1)));

        stored.EventId.Should().Be("sparse");
        stored.AgentId.Should().BeNull();
        stored.RunId.Should().BeNull();
        stored.ConceptName.Should().BeNull();
        stored.Transport.Should().BeNull();
        stored.ArgumentsShape.Should().BeNull();
        stored.Outcome.Should().BeNull();
        stored.Duration.Should().BeNull();
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static TrajectoryEvent NewEvent(
        string id,
        string? agentId = null,
        string? run = null,
        string? concept = null,
        string? transport = null,
        TrajectoryOutcome? outcome = null,
        IReadOnlyDictionary<string, string>? shape = null,
        DateTimeOffset? timestamp = null) =>
        new()
        {
            EventId = id,
            Timestamp = timestamp ?? DateTimeOffset.UtcNow,
            EventName = "tool.call",
            Operation = OntologyOperation.Call,
            AgentId = agentId,
            RunId = run,
            ConceptName = concept,
            Transport = transport,
            ArgumentsShape = shape,
            Outcome = outcome,
        };

    private static async Task<TrajectoryEvent> Single(IAsyncEnumerable<TrajectoryEvent> src)
    {
        await foreach (var e in src) return e;
        throw new InvalidOperationException("store empty");
    }

    private static async Task<List<TrajectoryEvent>> Collect(IAsyncEnumerable<TrajectoryEvent> src)
    {
        var list = new List<TrajectoryEvent>();
        await foreach (var e in src) list.Add(e);
        return list;
    }
}
