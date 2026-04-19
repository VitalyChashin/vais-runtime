// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;
using Orleans.TestingHost;
using Testcontainers.Redis;
using Vais.Agents.Hosting.Orleans;
using Xunit;

namespace Vais.Agents.Persistence.Redis.Tests;

/// <summary>
/// xUnit collection fixture: spins up a fresh <c>redis:7-alpine</c> container via
/// Testcontainers, then deploys an Orleans <see cref="TestCluster"/> that points its
/// grain-storage provider (<see cref="AiAgentGrain.StorageName"/>) at that Redis. One
/// Redis + one cluster per collection; both disposed at collection teardown.
/// </summary>
/// <remarks>
/// <para>
/// Requires Docker Desktop running — Testcontainers will fail fast if the Docker daemon
/// is unreachable. The container gets an ephemeral host port, so these tests can coexist
/// with a user's own Redis on :6379.
/// </para>
/// </remarks>
public sealed class RedisClusterFixture : IAsyncLifetime
{
    private RedisContainer _redis = null!;

    /// <summary>
    /// Connection string published to the <see cref="SiloConfigurator"/> via
    /// <see cref="CurrentConnectionString"/>. Exposed as an instance property
    /// for direct inspection from tests (e.g. connecting with StackExchange.Redis
    /// to verify grain state landed as a Redis key).
    /// </summary>
    public string ConnectionString { get; private set; } = null!;

    public TestCluster Cluster { get; private set; } = null!;

    /// <summary>
    /// Configurators run in a separate silo process-like context; they don't share
    /// instance state with the fixture. Static handoff is the simplest way to thread
    /// the Testcontainers-generated connection string into the silo build.
    /// </summary>
    internal static string CurrentConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        _redis = new RedisBuilder("redis:7-alpine").Build();
        await _redis.StartAsync();

        ConnectionString = _redis.GetConnectionString();
        CurrentConnectionString = ConnectionString;

        var builder = new TestClusterBuilder();
        builder.AddSiloBuilderConfigurator<SiloConfigurator>();
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
            siloBuilder.AddAgenticRedisGrainStorage(CurrentConnectionString);
            siloBuilder.ConfigureServices(services =>
            {
                services.AddSingleton<ICompletionProvider, HistorySizeProvider>();
                services.ConfigureAgentGrains();
            });
        }
    }
}

[CollectionDefinition(CollectionName)]
public sealed class RedisClusterCollection : ICollectionFixture<RedisClusterFixture>
{
    public const string CollectionName = "Redis+Orleans cluster";
}
