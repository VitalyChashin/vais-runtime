// Copyright (c) 2026 VAIS2 Platform contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.Extensions.Hosting;
using StackExchange.Redis;
using Vais2.Agents.Hosting.Orleans;

namespace Vais2.Agents.Persistence.Redis;

/// <summary>
/// Silo- and client-side DI extensions that wire Orleans' built-in Redis providers
/// into the conventions used by <see cref="AiAgentGrain"/>. Two behaviours consumers
/// can opt into independently: clustering (silo membership table in Redis) and grain
/// storage (grain state in Redis, under <see cref="AiAgentGrain.StorageName"/>).
/// </summary>
/// <remarks>
/// <para>
/// Stream providers are intentionally <i>not</i> exposed from this package — they
/// land alongside the agent-event contracts in a later milestone. Wire up a Redis
/// stream provider directly with Orleans' built-in extensions if you need one now.
/// </para>
/// <para>
/// Every extension accepts a connection string in <c>StackExchange.Redis</c> format
/// (e.g. <c>"localhost:6379"</c>, <c>"my-cache.redis.cache.windows.net:6380,ssl=true,password=..."</c>).
/// The string is parsed once into <see cref="ConfigurationOptions"/>.
/// </para>
/// </remarks>
public static class AgenticRedisPersistenceExtensions
{
    /// <summary>
    /// Use Redis as the silo membership table (clustering). Equivalent to
    /// <c>siloBuilder.UseRedisClustering(...)</c> with the connection string pre-parsed.
    /// </summary>
    public static ISiloBuilder UseAgenticRedisClustering(
        this ISiloBuilder siloBuilder,
        string connectionString)
    {
        ArgumentNullException.ThrowIfNull(siloBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        var options = ConfigurationOptions.Parse(connectionString);
        return siloBuilder.UseRedisClustering(opts => opts.ConfigurationOptions = options);
    }

    /// <summary>
    /// Use Redis as the membership-lookup source for a client. Equivalent to
    /// <c>clientBuilder.UseRedisClustering(...)</c>.
    /// </summary>
    public static IClientBuilder UseAgenticRedisClustering(
        this IClientBuilder clientBuilder,
        string connectionString)
    {
        ArgumentNullException.ThrowIfNull(clientBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        var options = ConfigurationOptions.Parse(connectionString);
        return clientBuilder.UseRedisClustering(opts => opts.ConfigurationOptions = options);
    }

    /// <summary>
    /// Register a Redis-backed grain storage provider under
    /// <see cref="AiAgentGrain.StorageName"/> — the name <see cref="AiAgentGrain"/>
    /// reads via its <c>[PersistentState("state", AiAgentGrain.StorageName)]</c>
    /// facet. Consumers that want additional grain storage names should call
    /// <c>AddRedisGrainStorage(name, ...)</c> directly from Orleans.
    /// </summary>
    public static ISiloBuilder AddAgenticRedisGrainStorage(
        this ISiloBuilder siloBuilder,
        string connectionString)
    {
        ArgumentNullException.ThrowIfNull(siloBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        var options = ConfigurationOptions.Parse(connectionString);
        return siloBuilder.AddRedisGrainStorage(
            AiAgentGrain.StorageName,
            opts => opts.ConfigurationOptions = options);
    }
}
