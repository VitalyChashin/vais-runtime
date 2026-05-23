// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Orleans.TestingHost;
using Xunit;

namespace Vais.Agents.Hosting.Orleans.Tests;

/// <summary>
/// Tests for <see cref="InvokeLeaseGrain"/> and <see cref="OrleansInvokeLeaseStore"/> (CTL-9/10):
/// cluster-wide session-mode call-token liveness, heartbeat extension, hard-ceiling cap, and release.
/// Uses an in-memory <see cref="TestCluster"/> with memory grain storage for
/// <see cref="AiAgentGrain.StorageName"/>.
/// </summary>
public sealed class InvokeLeaseGrainTests : IAsyncLifetime
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

    private static string NewLeaseId() => Guid.NewGuid().ToString("N");

    [Fact]
    public async Task Start_Then_IsLive_True()
    {
        var grain = _cluster.GrainFactory.GetGrain<IInvokeLeaseGrain>(NewLeaseId());
        await grain.StartAsync("run", "agent", sessionTtlSeconds: 300, heartbeatTtlSeconds: 180);
        (await grain.IsLiveAsync()).Should().BeTrue();
    }

    [Fact]
    public async Task Release_Makes_IsLive_False_From_Any_Reference()
    {
        var leaseId = NewLeaseId();
        var grain = _cluster.GrainFactory.GetGrain<IInvokeLeaseGrain>(leaseId);
        await grain.StartAsync("run", "agent", 300, 180);

        // A different reference for the same key routes to the same activation (cluster-wide).
        await _cluster.GrainFactory.GetGrain<IInvokeLeaseGrain>(leaseId).ReleaseAsync();

        (await grain.IsLiveAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Expired_SoftDeadline_IsLive_False()
    {
        var grain = _cluster.GrainFactory.GetGrain<IInvokeLeaseGrain>(NewLeaseId());
        await grain.StartAsync("run", "agent", sessionTtlSeconds: 300, heartbeatTtlSeconds: -1);
        (await grain.IsLiveAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task HardCeiling_Caps_Heartbeat()
    {
        var grain = _cluster.GrainFactory.GetGrain<IInvokeLeaseGrain>(NewLeaseId());
        await grain.StartAsync("run", "agent", sessionTtlSeconds: 0, heartbeatTtlSeconds: 10_000);
        await grain.HeartbeatAsync(10_000);
        (await grain.IsLiveAsync()).Should().BeFalse("heartbeat cannot push the lease past its hard ceiling");
    }

    [Fact]
    public async Task Store_RoundTrips_Start_IsLive_Release()
    {
        var store = new OrleansInvokeLeaseStore(_cluster.GrainFactory);
        var leaseId = NewLeaseId();

        await store.StartAsync(leaseId, "run", "agent", 300, 180);
        (await store.IsLiveAsync(leaseId)).Should().BeTrue();

        await store.HeartbeatAsync(leaseId, 180);
        (await store.IsLiveAsync(leaseId)).Should().BeTrue();

        await store.ReleaseAsync(leaseId);
        (await store.IsLiveAsync(leaseId)).Should().BeFalse();
    }

    private sealed class SiloConfigurator : ISiloConfigurator
    {
        public void Configure(ISiloBuilder siloBuilder)
            => siloBuilder.AddMemoryGrainStorage(AiAgentGrain.StorageName);
    }
}
