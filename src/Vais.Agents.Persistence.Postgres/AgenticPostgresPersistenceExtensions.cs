// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Vais.Agents.Eval;
using Vais.Agents.Hosting.Orleans;

namespace Vais.Agents.Persistence.Postgres;

/// <summary>
/// Silo- and client-side DI extensions that wire Orleans' ADO.NET providers into
/// <see cref="AiAgentGrain"/>'s conventions using <see cref="NpgsqlInvariant"/>
/// (the Npgsql ADO.NET provider) so consumers get Postgres-backed clustering and
/// grain storage without re-typing the invariant or the storage name.
/// </summary>
/// <remarks>
/// <para>
/// Orleans ADO.NET providers require the database schema to exist before the silo
/// starts. Run the schema scripts from the Orleans repository (<c>PostgreSQL-Main.sql</c>
/// plus the clustering / persistence variants) against your database — they ship only
/// in the Orleans source tree, not in the NuGet package. This library does not
/// auto-provision schema at runtime.
/// </para>
/// <para>
/// Streams are intentionally not offered here — same reason as in Redis persistence:
/// agent-event streaming waits for the neutral <c>IAgentEventBus</c> / <c>AgentEvent</c>
/// contracts in a later milestone.
/// </para>
/// </remarks>
public static class AgenticPostgresPersistenceExtensions
{
    /// <summary>
    /// The ADO.NET invariant name for the Npgsql provider, the value Orleans' ADO.NET
    /// providers expect in <c>options.Invariant</c> to address a PostgreSQL database.
    /// </summary>
    public const string NpgsqlInvariant = "Npgsql";

    /// <summary>
    /// Use Postgres (via ADO.NET + Npgsql) as the silo membership table (clustering).
    /// </summary>
    /// <remarks>
    /// Equivalent to <c>siloBuilder.UseAdoNetClustering(opts => { opts.Invariant = "Npgsql"; opts.ConnectionString = ...; })</c>.
    /// </remarks>
    public static ISiloBuilder UseAgenticPostgresClustering(
        this ISiloBuilder siloBuilder,
        string connectionString)
    {
        ArgumentNullException.ThrowIfNull(siloBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        return siloBuilder.UseAdoNetClustering(options =>
        {
            options.Invariant = NpgsqlInvariant;
            options.ConnectionString = connectionString;
        });
    }

    /// <summary>
    /// Use Postgres as the membership-lookup source for a client.
    /// </summary>
    public static IClientBuilder UseAgenticPostgresClustering(
        this IClientBuilder clientBuilder,
        string connectionString)
    {
        ArgumentNullException.ThrowIfNull(clientBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        return clientBuilder.UseAdoNetClustering(options =>
        {
            options.Invariant = NpgsqlInvariant;
            options.ConnectionString = connectionString;
        });
    }

    /// <summary>
    /// Register a Postgres-backed grain storage provider under
    /// <see cref="AiAgentGrain.StorageName"/> — the name <see cref="AiAgentGrain"/>
    /// reads via its <c>[PersistentState("state", AiAgentGrain.StorageName)]</c>
    /// facet. Consumers that want additional grain storage names should call
    /// <see cref="AddAgenticPostgresGrainStorage(ISiloBuilder, string, string)"/>.
    /// </summary>
    public static ISiloBuilder AddAgenticPostgresGrainStorage(
        this ISiloBuilder siloBuilder,
        string connectionString)
    {
        ArgumentNullException.ThrowIfNull(siloBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        return siloBuilder.AddAdoNetGrainStorage(
            AiAgentGrain.StorageName,
            options =>
            {
                options.Invariant = NpgsqlInvariant;
                options.ConnectionString = connectionString;
            });
    }

    /// <summary>
    /// Register a Postgres-backed grain storage provider under an arbitrary
    /// <paramref name="name"/>. Use this to add providers beyond
    /// <see cref="AiAgentGrain.StorageName"/> — for example <c>"PubSubStore"</c> when
    /// durable stream subscriptions are required in localhost mode.
    /// </summary>
    public static ISiloBuilder AddAgenticPostgresGrainStorage(
        this ISiloBuilder siloBuilder,
        string name,
        string connectionString)
    {
        ArgumentNullException.ThrowIfNull(siloBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        return siloBuilder.AddAdoNetGrainStorage(
            name,
            options =>
            {
                options.Invariant = NpgsqlInvariant;
                options.ConnectionString = connectionString;
            });
    }

    /// <summary>
    /// Register <see cref="PostgresEvalResultStore"/> as the singleton <see cref="IEvalResultStore"/>.
    /// Requires the tables from <c>Migrations/eval_results_tables.sql</c> to exist in the target database.
    /// </summary>
    /// <param name="services">The host's DI container.</param>
    /// <param name="connectionString">Postgres connection string.</param>
    public static IServiceCollection AddPostgresEvalResultStore(
        this IServiceCollection services,
        string connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        var ds = NpgsqlDataSource.Create(connectionString);
        services.TryAddSingleton<IEvalResultStore>(new PostgresEvalResultStore(ds));
        return services;
    }

    /// <summary>
    /// Register <see cref="PostgresGraphCheckpointer"/> as the singleton <see cref="IGraphCheckpointer"/> —
    /// a silo-free, Postgres-direct alternative to <c>OrleansCheckpointer</c> for hosts that run the graph
    /// orchestrator without an Orleans grain backing. Schema is applied automatically on first use.
    /// Uses <c>TryAddSingleton</c>; in a host that already registers an <see cref="IGraphCheckpointer"/>
    /// (e.g. the runtime's Orleans one), use <c>services.Replace(...)</c> instead to override it.
    /// </summary>
    /// <param name="services">The host's DI container.</param>
    /// <param name="connectionString">Postgres connection string.</param>
    public static IServiceCollection AddPostgresGraphCheckpointer(
        this IServiceCollection services,
        string connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        var ds = NpgsqlDataSource.Create(connectionString);
        services.TryAddSingleton<IGraphCheckpointer>(new PostgresGraphCheckpointer(ds));
        return services;
    }
}
