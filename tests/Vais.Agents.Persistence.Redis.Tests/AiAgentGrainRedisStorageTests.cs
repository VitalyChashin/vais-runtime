// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using StackExchange.Redis;
using Vais.Agents.Hosting.Orleans;
using Xunit;

namespace Vais.Agents.Persistence.Redis.Tests;

/// <summary>
/// End-to-end tests that prove <see cref="AiAgentGrain"/> stores and restores state
/// through the Redis grain-storage provider wired by
/// <see cref="AgenticRedisPersistenceExtensions.AddAgenticRedisGrainStorage"/>.
/// </summary>
[Collection(RedisClusterCollection.CollectionName)]
public sealed class AiAgentGrainRedisStorageTests
{
    private readonly RedisClusterFixture _fx;

    public AiAgentGrainRedisStorageTests(RedisClusterFixture fx) => _fx = fx;

    [Fact]
    public async Task Ask_Writes_History_To_Redis_And_Reads_It_Back()
    {
        var grain = _fx.Cluster.GrainFactory.GetGrain<IAiAgentGrain>("redis-ask-once");
        try
        {
            var reply = await grain.AskAsync("hello");

            reply.Should().Be("history-size=1");
            var history = await grain.GetHistoryAsync();
            history.Should().HaveCount(2);
            history[0].Text.Should().Be("hello");
            history[1].Text.Should().Be("history-size=1");
        }
        finally
        {
            await grain.DeleteAsync();
        }
    }

    [Fact]
    public async Task History_Persists_Across_Activation_Collection_Backed_By_Redis()
    {
        var grainId = "redis-persist";
        var grain = _fx.Cluster.GrainFactory.GetGrain<IAiAgentGrain>(grainId);
        try
        {
            await grain.AskAsync("turn-1");
            await grain.AskAsync("turn-2");
            (await grain.GetHistoryAsync()).Should().HaveCount(4);

            var mgmt = _fx.Cluster.GrainFactory.GetGrain<IManagementGrain>(0);
            await mgmt.ForceActivationCollection(TimeSpan.Zero);

            var rehydrated = _fx.Cluster.GrainFactory.GetGrain<IAiAgentGrain>(grainId);
            var rehistory = await rehydrated.GetHistoryAsync();
            rehistory.Should().HaveCount(4);

            // Provider sees 5 = 4 prior + new user turn → Redis rehydration worked.
            var reply = await rehydrated.AskAsync("turn-3");
            reply.Should().Be("history-size=5");
        }
        finally
        {
            await grain.DeleteAsync();
        }
    }

    [Fact]
    public async Task Grain_State_Materialises_As_A_Redis_Key()
    {
        var grainId = "redis-key-inspect";
        var grain = _fx.Cluster.GrainFactory.GetGrain<IAiAgentGrain>(grainId);
        try
        {
            await grain.AskAsync("hello");

            using var mux = await ConnectionMultiplexer.ConnectAsync(_fx.ConnectionString);
            var server = mux.GetServer(mux.GetEndPoints()[0]);
            var keys = server.Keys(pattern: "*").Select(k => (string)k!).ToList();

            keys.Should().NotBeEmpty("Redis grain-storage should have written at least one key");
            keys.Should().Contain(k => k.Contains(grainId, StringComparison.Ordinal),
                $"one of the keys should reference the grain id '{grainId}'");
        }
        finally
        {
            await grain.DeleteAsync();
        }
    }

    [Fact]
    public async Task Delete_Clears_State_So_Reactivation_Starts_Fresh_From_Redis()
    {
        var grainId = "redis-delete";
        var grain = _fx.Cluster.GrainFactory.GetGrain<IAiAgentGrain>(grainId);

        await grain.AskAsync("turn-1");
        (await grain.GetHistoryAsync()).Should().HaveCount(2);

        await grain.DeleteAsync();

        // Force the cluster to forget this activation; the next call reactivates
        // the grain, which reads whatever the Redis provider now returns for
        // ClearStateAsync's aftermath — either a missing key or an empty state.
        var mgmt = _fx.Cluster.GrainFactory.GetGrain<IManagementGrain>(0);
        await mgmt.ForceActivationCollection(TimeSpan.Zero);

        var reborn = _fx.Cluster.GrainFactory.GetGrain<IAiAgentGrain>(grainId);
        (await reborn.GetHistoryAsync()).Should().BeEmpty(
            "ClearStateAsync (via grain.DeleteAsync) must leave the grain with no history");
        (await reborn.GetSystemPromptAsync()).Should().BeNull();

        await reborn.DeleteAsync();
    }

    [Fact]
    public async Task SystemPrompt_Persists_Via_Redis_Across_Activation_Collection()
    {
        var grainId = "redis-prompt";
        var grain = _fx.Cluster.GrainFactory.GetGrain<IAiAgentGrain>(grainId);
        try
        {
            await grain.SetSystemPromptAsync("be-concise");

            var mgmt = _fx.Cluster.GrainFactory.GetGrain<IManagementGrain>(0);
            await mgmt.ForceActivationCollection(TimeSpan.Zero);

            var rehydrated = _fx.Cluster.GrainFactory.GetGrain<IAiAgentGrain>(grainId);
            (await rehydrated.GetSystemPromptAsync()).Should().Be("be-concise");
        }
        finally
        {
            await grain.DeleteAsync();
        }
    }
}
