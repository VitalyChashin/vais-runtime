// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

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
    /// <c>AddAdoNetGrainStorage(name, ...)</c> directly from Orleans.
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
}
