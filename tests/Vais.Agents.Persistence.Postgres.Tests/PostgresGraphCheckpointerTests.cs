// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using FluentAssertions;
using Npgsql;
using Xunit;

namespace Vais.Agents.Persistence.Postgres.Tests;

/// <summary>
/// Integration tests for <see cref="PostgresGraphCheckpointer"/> (§1e). Reuses the shared Postgres
/// container from <see cref="PostgresClusterFixture"/> (the checkpointer auto-creates its own
/// <c>vais_graph_checkpoints</c> table; no Orleans involvement). Each test uses a unique run id.
/// </summary>
[Collection(PostgresClusterCollection.CollectionName)]
public sealed class PostgresGraphCheckpointerTests
{
    private readonly PostgresClusterFixture _fx;

    public PostgresGraphCheckpointerTests(PostgresClusterFixture fx) => _fx = fx;

    private PostgresGraphCheckpointer NewCheckpointer()
        => new(NpgsqlDataSource.Create(_fx.ConnectionString));

    private static string NewRunId() => Guid.NewGuid().ToString("N");

    private static GraphCheckpoint Sample(string runId, int superStep = 2, bool complete = false, string? interruptId = null)
        => new(
            RunId: runId,
            GraphId: "graph-1",
            GraphVersion: "1.0",
            State: new Dictionary<string, JsonElement>
            {
                ["count"] = JsonSerializer.SerializeToElement(3),
                ["note"] = JsonSerializer.SerializeToElement("hello"),
            },
            NextNodeId: complete ? null : "next-node",
            SuperStepIndex: superStep,
            PendingInterruptId: interruptId,
            IsComplete: complete,
            CreatedAt: DateTimeOffset.UtcNow);

    [Fact]
    public async Task Save_Then_Load_RoundTrips_Including_State_And_Interrupt()
    {
        var checkpointer = NewCheckpointer();
        var runId = NewRunId();
        await checkpointer.SaveAsync(Sample(runId, superStep: 4, interruptId: "approval-1"));

        var loaded = await checkpointer.LoadAsync(runId);

        loaded.Should().NotBeNull();
        loaded!.RunId.Should().Be(runId);
        loaded.GraphId.Should().Be("graph-1");
        loaded.GraphVersion.Should().Be("1.0");
        loaded.NextNodeId.Should().Be("next-node");
        loaded.SuperStepIndex.Should().Be(4);
        loaded.PendingInterruptId.Should().Be("approval-1");
        loaded.IsComplete.Should().BeFalse();
        loaded.State["count"].GetInt32().Should().Be(3, "JsonElement state must survive the jsonb round-trip");
        loaded.State["note"].GetString().Should().Be("hello");
    }

    [Fact]
    public async Task Load_Unknown_Run_Returns_Null()
    {
        var checkpointer = NewCheckpointer();
        (await checkpointer.LoadAsync(NewRunId())).Should().BeNull();
    }

    [Fact]
    public async Task Save_Twice_Same_RunId_Upserts_Latest()
    {
        var checkpointer = NewCheckpointer();
        var runId = NewRunId();

        await checkpointer.SaveAsync(Sample(runId, superStep: 1));
        await checkpointer.SaveAsync(Sample(runId, superStep: 7, complete: true));

        var loaded = await checkpointer.LoadAsync(runId);
        loaded!.SuperStepIndex.Should().Be(7, "latest-only: the second save overwrites the first");
        loaded.IsComplete.Should().BeTrue();
        loaded.NextNodeId.Should().BeNull();
    }

    [Fact]
    public async Task Delete_Is_Idempotent()
    {
        var checkpointer = NewCheckpointer();
        var runId = NewRunId();
        await checkpointer.SaveAsync(Sample(runId));

        await checkpointer.DeleteAsync(runId);
        await checkpointer.DeleteAsync(runId); // second delete must not throw

        (await checkpointer.LoadAsync(runId)).Should().BeNull();
    }
}
