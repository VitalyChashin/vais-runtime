// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Vais.Agents.Core;
using Xunit;

namespace Vais.Agents.Runtime.Plugins.Container.Tests;

/// <summary>Unit tests for <see cref="InMemoryInvokeLeaseStore"/> and <see cref="LeaseLivenessCache"/>.</summary>
public sealed class InvokeLeaseStoreTests
{
    [Fact]
    public async Task Start_Then_IsLive_True()
    {
        var store = new InMemoryInvokeLeaseStore();
        await store.StartAsync("lease-1", "run", "agent", sessionTtlSeconds: 100, heartbeatTtlSeconds: 100);
        (await store.IsLiveAsync("lease-1")).Should().BeTrue();
    }

    [Fact]
    public async Task Unknown_Lease_IsLive_False()
    {
        var store = new InMemoryInvokeLeaseStore();
        (await store.IsLiveAsync("nope")).Should().BeFalse();
    }

    [Fact]
    public async Task Release_Makes_IsLive_False()
    {
        var store = new InMemoryInvokeLeaseStore();
        await store.StartAsync("lease-1", "run", "agent", 100, 100);
        await store.ReleaseAsync("lease-1");
        (await store.IsLiveAsync("lease-1")).Should().BeFalse();
    }

    [Fact]
    public async Task Expired_SoftDeadline_IsLive_False()
    {
        var store = new InMemoryInvokeLeaseStore();
        await store.StartAsync("lease-1", "run", "agent", sessionTtlSeconds: 100, heartbeatTtlSeconds: -1);
        (await store.IsLiveAsync("lease-1")).Should().BeFalse();
    }

    [Fact]
    public async Task HardCeiling_Caps_SoftDeadline()
    {
        // sessionTtl = 0 → hard ceiling is now; a generous heartbeat cannot push the lease past it.
        var store = new InMemoryInvokeLeaseStore();
        await store.StartAsync("lease-1", "run", "agent", sessionTtlSeconds: 0, heartbeatTtlSeconds: 10_000);
        (await store.IsLiveAsync("lease-1")).Should().BeFalse();
    }

    [Fact]
    public async Task Heartbeat_On_ReleasedLease_DoesNotRevive()
    {
        var store = new InMemoryInvokeLeaseStore();
        await store.StartAsync("lease-1", "run", "agent", 100, 100);
        await store.ReleaseAsync("lease-1");
        await store.HeartbeatAsync("lease-1", 100);
        (await store.IsLiveAsync("lease-1")).Should().BeFalse();
    }

    [Fact]
    public async Task Cache_Serves_Recent_Result_Without_Hitting_Store()
    {
        var store = new CountingLeaseStore(live: true);
        var cache = new LeaseLivenessCache(store, TimeSpan.FromSeconds(60));
        (await cache.IsLiveAsync("lease-1")).Should().BeTrue();
        (await cache.IsLiveAsync("lease-1")).Should().BeTrue();
        store.IsLiveCalls.Should().Be(1, "the second check is served from the cache window");
    }

    [Fact]
    public async Task Cache_With_Zero_Window_Always_Reflects_Store()
    {
        var store = new CountingLeaseStore(live: true);
        var cache = new LeaseLivenessCache(store, TimeSpan.Zero);
        (await cache.IsLiveAsync("lease-1")).Should().BeTrue();
        store.Live = false;
        (await cache.IsLiveAsync("lease-1")).Should().BeFalse();
        store.IsLiveCalls.Should().Be(2);
    }

    private sealed class CountingLeaseStore(bool live) : IInvokeLeaseStore
    {
        public bool Live { get; set; } = live;
        public int IsLiveCalls { get; private set; }

        public ValueTask StartAsync(string leaseId, string runId, string agentId, int sessionTtlSeconds, int heartbeatTtlSeconds, CancellationToken ct = default)
            => ValueTask.CompletedTask;
        public ValueTask<bool> IsLiveAsync(string leaseId, CancellationToken ct = default)
        {
            IsLiveCalls++;
            return ValueTask.FromResult(Live);
        }
        public ValueTask HeartbeatAsync(string leaseId, int heartbeatTtlSeconds, CancellationToken ct = default)
            => ValueTask.CompletedTask;
        public ValueTask ReleaseAsync(string leaseId, CancellationToken ct = default)
            => ValueTask.CompletedTask;
    }
}
