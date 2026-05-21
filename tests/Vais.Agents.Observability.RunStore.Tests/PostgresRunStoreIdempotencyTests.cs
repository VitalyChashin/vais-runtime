// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Xunit;

namespace Vais.Agents.Observability.RunStore.Tests;

/// <summary>
/// Integration tests for <see cref="PostgresRunStore"/> idempotency under duplicate event
/// delivery (G3 — `plans/runstore-duplicate-persistence-gap-2026-05-21.md`). On a cross-silo
/// stream provider every silo's <see cref="RunStoreSubscriber"/> receives every event, so each
/// write reaches the store once per silo. Replaying every write twice here simulates a two-silo
/// cluster; the stored state must match a single delivery.
/// </summary>
/// <remarks>Requires Docker (Testcontainers spins an ephemeral <c>postgres:16-alpine</c>).</remarks>
public sealed class PostgresRunStoreIdempotencyTests : IAsyncLifetime
{
    private PostgreSqlContainer _postgres = null!;
    private ServiceProvider _provider = null!;
    private IRunStore _store = null!;

    public async Task InitializeAsync()
    {
        _postgres = new PostgreSqlBuilder("postgres:16-alpine").Build();
        await _postgres.StartAsync();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRunStore(o =>
        {
            o.ConnectionString = _postgres.GetConnectionString();
            o.RetentionDays = 30;
        });
        _provider = services.BuildServiceProvider();
        _store = _provider.GetRequiredService<IRunStore>();
        await _store.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        await _provider.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    [Fact]
    public async Task DuplicateDelivery_Of_Full_Run_Is_Idempotent()
    {
        // Replay the whole event sequence for one run twice (simulating two silos each
        // delivering every event), then assert the stored state matches a single delivery.
        for (var silo = 0; silo < 2; silo++)
        {
            await _store.StartRunAsync("run-1", "graph-1");
            await _store.StartNodeAsync("run-1", "work", "Agent", "agent-x");
            await _store.RecordNodeInvocationAsync("run-1", "work", "agent-x", "in", "out", 11, 22);
            await _store.RecordEdgeAsync("run-1", "work", "end");
            await _store.CompleteNodeAsync("run-1", "work");
            await _store.CompleteRunAsync("run-1", 3);
        }

        var run = await _store.GetRunAsync("run-1");
        run.Should().NotBeNull();
        run!.Status.Should().Be(RunStatus.Completed);
        run.SuperSteps.Should().Be(3);

        var nodes = await _store.GetNodesAsync("run-1");
        nodes.Should().ContainSingle("a duplicate StartNode must upsert, not insert a second row");
        var node = nodes[0];
        node.NodeId.Should().Be("work");
        node.InputTokens.Should().Be(11);
        node.OutputTokens.Should().Be(22);
        node.EdgesTaken.Should().Equal(new[] { "end" }); // not ["end","end"] — duplicate delivery deduped
    }

    [Fact]
    public async Task Distinct_Edge_Targets_Are_All_Preserved()
    {
        // Idempotency dedups identical targets, but genuinely different targets out of the same
        // node (e.g. a loop edge then the exit edge) must all be recorded.
        await _store.StartRunAsync("run-2", "graph-1");
        await _store.StartNodeAsync("run-2", "grade", "Agent", null);

        await _store.RecordEdgeAsync("run-2", "grade", "retrieve"); // loop
        await _store.RecordEdgeAsync("run-2", "grade", "retrieve"); // duplicate delivery — collapses
        await _store.RecordEdgeAsync("run-2", "grade", "end");      // exit

        var node = await _store.GetNodeAsync("run-2", "grade");
        node.Should().NotBeNull();
        node!.EdgesTaken.Should().BeEquivalentTo(new[] { "retrieve", "end" });
    }
}
