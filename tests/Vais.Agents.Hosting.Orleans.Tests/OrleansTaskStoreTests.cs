// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using A2A;
using FluentAssertions;
using Xunit;

namespace Vais.Agents.Hosting.Orleans.Tests;

/// <summary>
/// v0.8 PR 3: Orleans-backed A2A task persistence. Covers the round-trip pattern
/// (save → get produces equivalent task, including <c>input-required</c> state) and
/// the resume-across-restart invariant (task survives simulated silo restart).
/// </summary>
[Collection(OrleansClusterCollection.CollectionName)]
public sealed class OrleansTaskStoreTests
{
    private readonly OrleansClusterFixture _fixture;

    public OrleansTaskStoreTests(OrleansClusterFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task SaveTask_Then_GetTask_Round_Trips_InputRequired_State()
    {
        var store = new OrleansTaskStore(_fixture.Cluster.Client);
        var taskId = $"task-{Guid.NewGuid():N}";
        var original = new AgentTask
        {
            Id = taskId,
            ContextId = "ctx-round-trip",
            Status = new A2A.TaskStatus
            {
                State = TaskState.InputRequired,
                Timestamp = DateTimeOffset.UtcNow,
            },
        };

        await store.SaveTaskAsync(taskId, original, CancellationToken.None);
        var loaded = await store.GetTaskAsync(taskId, CancellationToken.None);

        loaded.Should().NotBeNull();
        loaded!.Id.Should().Be(taskId);
        loaded.ContextId.Should().Be("ctx-round-trip");
        loaded.Status.State.Should().Be(TaskState.InputRequired);
    }

    [Fact]
    public async Task GetTask_For_Unknown_Id_Returns_Null()
    {
        var store = new OrleansTaskStore(_fixture.Cluster.Client);

        var loaded = await store.GetTaskAsync($"ghost-{Guid.NewGuid():N}", CancellationToken.None);

        loaded.Should().BeNull();
    }

    [Fact]
    public async Task Task_Survives_Simulated_Silo_Restart_With_Memory_Storage()
    {
        // Memory grain storage persists for the lifetime of the process but clears
        // state between grain activations. We save, deactivate the grain (simulating
        // eviction), then re-fetch — the grain re-reads its IPersistentState from
        // the same in-memory store on activation, so the task should come back intact.
        var store = new OrleansTaskStore(_fixture.Cluster.Client);
        var taskId = $"task-restart-{Guid.NewGuid():N}";
        await store.SaveTaskAsync(taskId, new AgentTask
        {
            Id = taskId,
            ContextId = "ctx-restart",
            Status = new A2A.TaskStatus { State = TaskState.InputRequired, Timestamp = DateTimeOffset.UtcNow },
        }, CancellationToken.None);

        // Force activation collection — grain loses its activation; IPersistentState holds
        // the durable record, so the next call re-activates and reads it back.
        var management = _fixture.Cluster.Client.GetGrain<global::Orleans.Runtime.IManagementGrain>(0);
        await management.ForceActivationCollection(TimeSpan.Zero);

        var reloaded = await store.GetTaskAsync(taskId, CancellationToken.None);
        reloaded.Should().NotBeNull();
        reloaded!.Id.Should().Be(taskId);
        reloaded.Status.State.Should().Be(TaskState.InputRequired);
    }
}
