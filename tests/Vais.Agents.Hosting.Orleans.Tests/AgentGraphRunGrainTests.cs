// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Orleans.TestingHost;
using Vais.Agents.Control;
using Xunit;

namespace Vais.Agents.Hosting.Orleans.Tests;

/// <summary>
/// Tests for <see cref="AgentGraphRunGrain"/> and <see cref="OrleansGraphRunCoordinator"/> (G4 GR-B):
/// cluster-wide run conflict detection, cancellation signalling, and status. Uses an in-memory
/// <see cref="TestCluster"/> with memory grain storage for <see cref="AiAgentGrain.StorageName"/>.
/// </summary>
public sealed class AgentGraphRunGrainTests : IAsyncLifetime
{
    private TestCluster _cluster = null!;

    public async Task InitializeAsync()
    {
        var builder = new TestClusterBuilder();
        builder.AddSiloBuilderConfigurator<SiloConfigurator>();
        _cluster = builder.Build();
        await _cluster.DeployAsync();
    }

    public async Task DisposeAsync()
    {
        if (_cluster is not null)
        {
            await _cluster.StopAllSilosAsync();
            await _cluster.DisposeAsync();
        }
    }

    private static string NewRunId() => Guid.NewGuid().ToString("N");

    [Fact]
    public async Task TryStart_Is_Idempotent_Conflict()
    {
        var grain = _cluster.GrainFactory.GetGrain<IAgentGraphRunGrain>(NewRunId());

        (await grain.TryStartAsync("graph-1", "1.0")).Should().BeTrue("first start wins");
        (await grain.TryStartAsync("graph-1", "1.0")).Should().BeFalse("a second concurrent start is a conflict");
    }

    [Fact]
    public async Task RequestCancel_Sets_Flag_Observable_By_Any_Caller()
    {
        var runId = NewRunId();
        var grain = _cluster.GrainFactory.GetGrain<IAgentGraphRunGrain>(runId);
        await grain.TryStartAsync("graph-1", "1.0");

        (await grain.IsCancelRequestedAsync()).Should().BeFalse();

        // A different grain reference for the same key routes to the same activation (cluster-wide).
        await _cluster.GrainFactory.GetGrain<IAgentGraphRunGrain>(runId).RequestCancelAsync();

        (await grain.IsCancelRequestedAsync()).Should().BeTrue("cancel is durable and key-addressable");
    }

    [Fact]
    public async Task Complete_Clears_Active_So_Get_Returns_Null()
    {
        var grain = _cluster.GrainFactory.GetGrain<IAgentGraphRunGrain>(NewRunId());
        await grain.TryStartAsync("graph-1", "1.0");

        (await grain.GetAsync()).Should().NotBeNull();

        await grain.CompleteAsync();

        (await grain.GetAsync()).Should().BeNull("a completed run is no longer active");
    }

    [Fact]
    public async Task Coordinator_RoundTrips_Start_Status_Cancel_Complete()
    {
        var coordinator = new OrleansGraphRunCoordinator(_cluster.GrainFactory);
        var runId = NewRunId();

        (await coordinator.TryStartAsync(runId, "graph-7", "2.0")).Should().BeTrue();
        (await coordinator.TryStartAsync(runId, "graph-7", "2.0")).Should().BeFalse();

        var snap = await coordinator.GetAsync(runId);
        snap.Should().NotBeNull();
        snap!.RunId.Should().Be(runId);
        snap.GraphId.Should().Be("graph-7");
        snap.Version.Should().Be("2.0");
        snap.CancelRequested.Should().BeFalse();

        await coordinator.RequestCancelAsync(runId);
        (await coordinator.IsCancelRequestedAsync(runId)).Should().BeTrue();
        (await coordinator.GetAsync(runId))!.CancelRequested.Should().BeTrue();

        await coordinator.CompleteAsync(runId, GraphRunOutcome.Completed);
        (await coordinator.GetAsync(runId)).Should().BeNull();
    }

    [Fact]
    public async Task MarkActive_Reactivates_And_Clears_Prior_Cancel()
    {
        var coordinator = new OrleansGraphRunCoordinator(_cluster.GrainFactory);
        var runId = NewRunId();

        await coordinator.TryStartAsync(runId, "g", "1.0");
        await coordinator.RequestCancelAsync(runId);
        await coordinator.CompleteAsync(runId, GraphRunOutcome.Interrupted);

        // Resume path: MarkActive re-registers the run and must not carry the stale cancel flag.
        await coordinator.MarkActiveAsync(runId, "g", "1.0");

        (await coordinator.IsCancelRequestedAsync(runId)).Should().BeFalse();
        (await coordinator.GetAsync(runId)).Should().NotBeNull();
    }

    private sealed class SiloConfigurator : ISiloConfigurator
    {
        public void Configure(ISiloBuilder siloBuilder)
            => siloBuilder.AddMemoryGrainStorage(AiAgentGrain.StorageName);
    }
}
