// Copyright (c) 2026 VAIS2 Platform contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Xunit;

namespace Vais2.Agents.Hosting.Orleans.Tests;

[Collection(OrleansClusterCollection.CollectionName)]
public sealed class OrleansAgentRuntimeTests
{
    private readonly OrleansClusterFixture _fx;

    public OrleansAgentRuntimeTests(OrleansClusterFixture fx) => _fx = fx;

    [Fact]
    public async Task Runtime_GetOrCreate_Returns_Proxy_That_Forwards_To_Grain()
    {
        var runtime = new OrleansAgentRuntime(_fx.Cluster.GrainFactory);

        var agent = runtime.GetOrCreate("runtime-agent-1");
        try
        {
            var reply = await agent.AskAsync("hello");
            reply.Should().Be("history-size=1");

            agent.History.Should().HaveCount(2);
            agent.History[0].Text.Should().Be("hello");
            agent.History[1].Text.Should().Be("history-size=1");
        }
        finally
        {
            runtime.Remove("runtime-agent-1");
        }
    }

    [Fact]
    public void Runtime_GetOrCreate_Returns_Same_Proxy_For_Same_Id()
    {
        var runtime = new OrleansAgentRuntime(_fx.Cluster.GrainFactory);

        var a = runtime.GetOrCreate("runtime-dedup");
        var b = runtime.GetOrCreate("runtime-dedup");

        a.Should().BeSameAs(b);
        runtime.Remove("runtime-dedup");
    }

    [Fact]
    public async Task Runtime_Remove_Clears_Grain_State_And_Evicts_Proxy()
    {
        var runtime = new OrleansAgentRuntime(_fx.Cluster.GrainFactory);

        var agent = runtime.GetOrCreate("runtime-remove");
        await agent.AskAsync("turn-1");
        agent.History.Should().HaveCount(2);

        var removed = runtime.Remove("runtime-remove");
        removed.Should().BeTrue();

        // Give Orleans a beat for the fire-and-forget DeleteAsync to settle.
        await Task.Delay(100);

        // A fresh GetOrCreate should produce a new proxy (the old was evicted) backed by
        // a grain with no persisted state.
        var fresh = runtime.GetOrCreate("runtime-remove");
        fresh.Should().NotBeSameAs(agent);
        fresh.History.Should().BeEmpty();

        runtime.Remove("runtime-remove");
    }

    [Fact]
    public void Runtime_TryGet_Reports_Cached_Proxies_Only()
    {
        var runtime = new OrleansAgentRuntime(_fx.Cluster.GrainFactory);

        runtime.TryGet("runtime-try-get", out _).Should().BeFalse();

        var agent = runtime.GetOrCreate("runtime-try-get");
        runtime.TryGet("runtime-try-get", out var found).Should().BeTrue();
        found.Should().BeSameAs(agent);

        runtime.Remove("runtime-try-get");
    }
}
