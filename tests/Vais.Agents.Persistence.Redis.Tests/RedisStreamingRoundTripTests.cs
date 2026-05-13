// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Orleans.TestingHost;
using Testcontainers.Redis;
using Vais.Agents.Hosting.Orleans;
using Xunit;

namespace Vais.Agents.Persistence.Redis.Tests;

/// <summary>
/// End-to-end test: <see cref="AgenticRedisPersistenceExtensions.UseAgenticRedisStreaming(ISiloBuilder, string)"/>
/// wires a Redis-backed Orleans stream provider under
/// <see cref="OrleansAgentEventBus.StreamNamespace"/>. Publishing an <see cref="AgentEvent"/>
/// through <see cref="OrleansAgentEventBus"/> must round-trip to a subscriber through a
/// real Redis container.
/// </summary>
/// <remarks>
/// Microsoft.Orleans.Streaming.Redis is still alpha-only at 10.1.0-alpha.1. Treat this
/// test as both feature coverage and a canary for the alpha package.
/// </remarks>
[Collection(RedisStreamingCollection.CollectionName)]
public sealed class RedisStreamingRoundTripTests
{
    private readonly RedisStreamingFixture _fx;

    public RedisStreamingRoundTripTests(RedisStreamingFixture fx) => _fx = fx;

    [Fact]
    public async Task Publishes_TurnStarted_Through_Real_Redis_Stream_Provider()
    {
        var bus = new OrleansAgentEventBus(_fx.Cluster.Client, OrleansAgentEventBus.StreamNamespace);

        var tcs = new TaskCompletionSource<AgentEvent>();
        using var subscription = bus.Subscribe((e, _) =>
        {
            tcs.TrySetResult(e);
            return ValueTask.CompletedTask;
        });

        var published = new TurnStarted(
            DateTimeOffset.UtcNow,
            new AgentContext(UserId: "u", TenantId: "t", AgentName: "a"),
            UserMessage: "hello over redis");
        await bus.PublishAsync(published);

        var received = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(15));
        received.Should().BeOfType<TurnStarted>()
            .Which.UserMessage.Should().Be("hello over redis");
    }

    [Fact]
    public async Task Round_Trips_All_Three_AgentEvent_Subclasses()
    {
        var bus = new OrleansAgentEventBus(_fx.Cluster.Client, OrleansAgentEventBus.StreamNamespace);

        var received = new List<AgentEvent>();
        var expectedCount = 3;
        var done = new TaskCompletionSource();
        using var subscription = bus.Subscribe((e, _) =>
        {
            lock (received)
            {
                received.Add(e);
                if (received.Count >= expectedCount)
                {
                    done.TrySetResult();
                }
            }
            return ValueTask.CompletedTask;
        });

        var started = new TurnStarted(DateTimeOffset.UtcNow, AgentContext.Empty, "msg");
        var completed = new TurnCompleted(DateTimeOffset.UtcNow, AgentContext.Empty, "reply", "gpt-x", 5, 3, TimeSpan.FromMilliseconds(17));
        var failed = new TurnFailed(DateTimeOffset.UtcNow, AgentContext.Empty, "BoomException", "kaboom", TimeSpan.FromMilliseconds(2));

        await bus.PublishAsync(started);
        await bus.PublishAsync(completed);
        await bus.PublishAsync(failed);

        await done.Task.WaitAsync(TimeSpan.FromSeconds(15));

        received.OfType<TurnStarted>().Should().ContainSingle()
            .Which.UserMessage.Should().Be("msg");
        received.OfType<TurnCompleted>().Should().ContainSingle()
            .Which.AssistantText.Should().Be("reply");
        received.OfType<TurnFailed>().Should().ContainSingle()
            .Which.ErrorMessage.Should().Be("kaboom");
    }
}

/// <summary>
/// xUnit collection fixture: spins up a fresh <c>redis:7-alpine</c> container via
/// Testcontainers, then deploys an Orleans <see cref="TestCluster"/> with
/// <see cref="AgenticRedisPersistenceExtensions.UseAgenticRedisStreaming(ISiloBuilder, string)"/>
/// on both silo and client sides, plus the <c>PubSubStore</c> memory grain storage that
/// Orleans streams require.
/// </summary>
public sealed class RedisStreamingFixture : IAsyncLifetime
{
    private RedisContainer _redis = null!;

    public TestCluster Cluster { get; private set; } = null!;

    internal static string CurrentConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        _redis = new RedisBuilder("redis:7-alpine").Build();
        await _redis.StartAsync();

        CurrentConnectionString = _redis.GetConnectionString();

        var builder = new TestClusterBuilder();
        builder.AddSiloBuilderConfigurator<SiloConfigurator>();
        builder.AddClientBuilderConfigurator<ClientConfigurator>();
        Cluster = builder.Build();
        await Cluster.DeployAsync();
    }

    public async Task DisposeAsync()
    {
        if (Cluster is not null)
        {
            await Cluster.StopAllSilosAsync();
            await Cluster.DisposeAsync();
        }

        if (_redis is not null)
        {
            await _redis.DisposeAsync();
        }
    }

    private sealed class SiloConfigurator : ISiloConfigurator
    {
        public void Configure(ISiloBuilder siloBuilder)
        {
            siloBuilder.UseAgenticRedisStreaming(CurrentConnectionString);
            // Orleans streams require a grain storage provider named exactly "PubSubStore"
            // for the PubSub grain's state. Memory storage is fine for tests.
            siloBuilder.AddMemoryGrainStorage("PubSubStore");
        }
    }

    private sealed class ClientConfigurator : IClientBuilderConfigurator
    {
        public void Configure(IConfiguration configuration, IClientBuilder clientBuilder)
        {
            clientBuilder.UseAgenticRedisStreaming(CurrentConnectionString);
        }
    }
}

[CollectionDefinition(CollectionName)]
public sealed class RedisStreamingCollection : ICollectionFixture<RedisStreamingFixture>
{
    public const string CollectionName = "Redis streaming cluster";
}
