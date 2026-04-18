// Copyright (c) 2026 VAIS2 Platform contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Orleans.Hosting;
using Orleans.TestingHost;
using Testcontainers.PostgreSql;
using Vais2.Agents.Hosting.Orleans;
using Xunit;

namespace Vais2.Agents.Persistence.Postgres.Tests;

/// <summary>
/// xUnit collection fixture: spins up a fresh <c>postgres:16-alpine</c> container via
/// Testcontainers, applies the two Orleans schema scripts (Main + Persistence) that ship
/// only in the Orleans source tree, then deploys an Orleans <see cref="TestCluster"/>
/// whose grain-storage provider is Postgres — addressing <see cref="AiAgentGrain.StorageName"/>.
/// </summary>
/// <remarks>
/// Requires Docker Desktop running. Testcontainers binds an ephemeral host port, so this
/// fixture coexists with any Postgres the user has on :5432.
/// </remarks>
public sealed class PostgresClusterFixture : IAsyncLifetime
{
    private PostgreSqlContainer _postgres = null!;

    public string ConnectionString { get; private set; } = null!;
    public TestCluster Cluster { get; private set; } = null!;

    internal static string CurrentConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        _postgres = new PostgreSqlBuilder("postgres:16-alpine").Build();
        await _postgres.StartAsync();

        ConnectionString = _postgres.GetConnectionString();
        CurrentConnectionString = ConnectionString;

        await ApplyOrleansSchemaAsync(ConnectionString);

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

        if (_postgres is not null)
        {
            await _postgres.DisposeAsync();
        }
    }

    private static async Task ApplyOrleansSchemaAsync(string connectionString)
    {
        var assembly = Assembly.GetExecutingAssembly();

        // Order matters: Main creates the OrleansQuery table + helper functions that
        // Persistence depends on (the stored procs register themselves as rows there).
        foreach (var resourceName in new[]
        {
            "Vais2.Agents.Persistence.Postgres.Tests.Sql.PostgreSQL-Main.sql",
            "Vais2.Agents.Persistence.Postgres.Tests.Sql.PostgreSQL-Persistence.sql",
        })
        {
            await using var stream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"Embedded SQL resource not found: {resourceName}");
            using var reader = new StreamReader(stream);
            var sql = await reader.ReadToEndAsync();

            await using var conn = new NpgsqlConnection(connectionString);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(sql, conn);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    private sealed class SiloConfigurator : ISiloConfigurator
    {
        public void Configure(ISiloBuilder siloBuilder)
        {
            siloBuilder.AddAgenticPostgresGrainStorage(CurrentConnectionString);
            siloBuilder.ConfigureServices(services =>
            {
                services.AddSingleton<ICompletionProvider, HistorySizeProvider>();
                services.ConfigureAgentGrains();
            });
        }
    }
}

[CollectionDefinition(CollectionName)]
public sealed class PostgresClusterCollection : ICollectionFixture<PostgresClusterFixture>
{
    public const string CollectionName = "Postgres+Orleans cluster";
}
