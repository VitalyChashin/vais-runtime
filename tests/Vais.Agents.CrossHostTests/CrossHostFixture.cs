// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Orleans.Hosting;
using Orleans.TestingHost;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;
using Vais.Agents.Core;
using Vais.Agents.Hosting.InMemory;
using Vais.Agents.Hosting.Orleans;
using Vais.Agents.Persistence.Postgres;
using Vais.Agents.Persistence.Redis;
using Xunit;

namespace Vais.Agents.CrossHostTests;

/// <summary>
/// Collection fixture for the cross-host parity suite. Spins up three runtimes
/// backed by the same deterministic <see cref="HistorySizeProvider"/>:
/// <list type="bullet">
///   <item>in-process <see cref="InMemoryAgentRuntime"/>,</item>
///   <item>an Orleans <see cref="TestCluster"/> with Redis grain storage (via Testcontainers),</item>
///   <item>a second Orleans <see cref="TestCluster"/> with Postgres grain storage (via Testcontainers).</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// The fixture owns three pairs of <see cref="RecordingUsageSink"/>/<see cref="RecordingFilter"/>
/// — one per host — so a scenario run can snapshot each host in isolation and the
/// test can diff the results. Tests call <see cref="ClearRecordings"/> between
/// scenarios; recorders have cluster lifetime so we can't give each test a fresh
/// silo DI container without re-deploying.
/// </para>
/// <para>
/// Silo-side DI wiring uses static handoff (same trick the Redis and Postgres test
/// projects already use for their connection strings) because <see cref="ISiloConfigurator"/>
/// instances are constructed by Orleans with a parameterless default — there is no
/// clean way to close over fixture-owned instances.
/// </para>
/// <para>
/// Requires Docker Desktop running. Both containers bind ephemeral host ports so
/// this fixture coexists with whatever the developer already has on :5432 / :6379.
/// </para>
/// </remarks>
public sealed class CrossHostFixture : IAsyncLifetime
{
    private RedisContainer _redis = null!;
    private PostgreSqlContainer _postgres = null!;

    public TestCluster RedisCluster { get; private set; } = null!;
    public TestCluster PostgresCluster { get; private set; } = null!;

    public RecordingUsageSink InMemorySink { get; } = new();
    public RecordingFilter InMemoryFilter { get; } = new();
    public RecordingUsageSink RedisSink { get; } = new();
    public RecordingFilter RedisFilter { get; } = new();
    public RecordingUsageSink PostgresSink { get; } = new();
    public RecordingFilter PostgresFilter { get; } = new();

    public IAgentRuntime InMemoryRuntime { get; private set; } = null!;

    public IAgentRuntime RedisRuntime => new OrleansAgentRuntime(RedisCluster.GrainFactory);
    public IAgentRuntime PostgresRuntime => new OrleansAgentRuntime(PostgresCluster.GrainFactory);

    // Silo-configurator handoffs — read at silo build time, set before each DeployAsync.
    internal static RecordingUsageSink? CurrentSink;
    internal static RecordingFilter? CurrentFilter;
    internal static string CurrentRedisConnectionString = string.Empty;
    internal static string CurrentPostgresConnectionString = string.Empty;

    public async Task InitializeAsync()
    {
        _redis = new RedisBuilder("redis:7-alpine").Build();
        _postgres = new PostgreSqlBuilder("postgres:16-alpine").Build();
        await Task.WhenAll(_redis.StartAsync(), _postgres.StartAsync()).ConfigureAwait(false);

        CurrentRedisConnectionString = _redis.GetConnectionString();
        CurrentPostgresConnectionString = _postgres.GetConnectionString();

        await ApplyOrleansPostgresSchemaAsync(CurrentPostgresConnectionString).ConfigureAwait(false);

        InMemoryRuntime = new InMemoryAgentRuntime(
            new HistorySizeProvider(),
            id => new StatefulAgentOptions
            {
                AgentName = id,
                UsageSink = InMemorySink,
                Filters = new IAgentFilter[] { InMemoryFilter },
            });

        CurrentSink = RedisSink;
        CurrentFilter = RedisFilter;
        var redisBuilder = new TestClusterBuilder();
        redisBuilder.AddSiloBuilderConfigurator<RedisSiloConfigurator>();
        RedisCluster = redisBuilder.Build();
        await RedisCluster.DeployAsync().ConfigureAwait(false);

        CurrentSink = PostgresSink;
        CurrentFilter = PostgresFilter;
        var postgresBuilder = new TestClusterBuilder();
        postgresBuilder.AddSiloBuilderConfigurator<PostgresSiloConfigurator>();
        PostgresCluster = postgresBuilder.Build();
        await PostgresCluster.DeployAsync().ConfigureAwait(false);
    }

    public async Task DisposeAsync()
    {
        if (RedisCluster is not null)
        {
            await RedisCluster.StopAllSilosAsync().ConfigureAwait(false);
            await RedisCluster.DisposeAsync().ConfigureAwait(false);
        }
        if (PostgresCluster is not null)
        {
            await PostgresCluster.StopAllSilosAsync().ConfigureAwait(false);
            await PostgresCluster.DisposeAsync().ConfigureAwait(false);
        }
        if (_redis is not null)
        {
            await _redis.DisposeAsync().ConfigureAwait(false);
        }
        if (_postgres is not null)
        {
            await _postgres.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Scrub every recorder. Scenarios call this at entry so the per-host snapshots
    /// only contain the turns the scenario itself drove.
    /// </summary>
    public void ClearRecordings()
    {
        InMemorySink.Clear();
        InMemoryFilter.Clear();
        RedisSink.Clear();
        RedisFilter.Clear();
        PostgresSink.Clear();
        PostgresFilter.Clear();
    }

    private static async Task ApplyOrleansPostgresSchemaAsync(string connectionString)
    {
        var assembly = Assembly.GetExecutingAssembly();
        foreach (var resourceName in new[]
        {
            "Vais.Agents.CrossHostTests.Sql.PostgreSQL-Main.sql",
            "Vais.Agents.CrossHostTests.Sql.PostgreSQL-Persistence.sql",
        })
        {
            await using var stream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"Embedded SQL resource not found: {resourceName}");
            using var reader = new StreamReader(stream);
            var sql = await reader.ReadToEndAsync().ConfigureAwait(false);

            await using var conn = new NpgsqlConnection(connectionString);
            await conn.OpenAsync().ConfigureAwait(false);
            await using var cmd = new NpgsqlCommand(sql, conn);
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
    }

    private sealed class RedisSiloConfigurator : ISiloConfigurator
    {
        public void Configure(ISiloBuilder siloBuilder)
        {
            siloBuilder.AddAgenticRedisGrainStorage(CurrentRedisConnectionString);
            var sink = CurrentSink!;
            var filter = CurrentFilter!;
            siloBuilder.ConfigureServices(services =>
            {
                services.AddSingleton<ICompletionProvider, HistorySizeProvider>();
                services.AddSingleton<IUsageSink>(sink);
                services.AddSingleton<IAgentFilter>(filter);
                services.ConfigureAgentGrains((sp, id) => new StatefulAgentOptions
                {
                    AgentName = id,
                    UsageSink = sp.GetRequiredService<IUsageSink>(),
                    Filters = sp.GetServices<IAgentFilter>().ToArray(),
                });
            });
        }
    }

    private sealed class PostgresSiloConfigurator : ISiloConfigurator
    {
        public void Configure(ISiloBuilder siloBuilder)
        {
            siloBuilder.AddAgenticPostgresGrainStorage(CurrentPostgresConnectionString);
            var sink = CurrentSink!;
            var filter = CurrentFilter!;
            siloBuilder.ConfigureServices(services =>
            {
                services.AddSingleton<ICompletionProvider, HistorySizeProvider>();
                services.AddSingleton<IUsageSink>(sink);
                services.AddSingleton<IAgentFilter>(filter);
                services.ConfigureAgentGrains((sp, id) => new StatefulAgentOptions
                {
                    AgentName = id,
                    UsageSink = sp.GetRequiredService<IUsageSink>(),
                    Filters = sp.GetServices<IAgentFilter>().ToArray(),
                });
            });
        }
    }
}

[CollectionDefinition(CollectionName)]
public sealed class CrossHostCollection : ICollectionFixture<CrossHostFixture>
{
    public const string CollectionName = "Cross-host parity";
}
