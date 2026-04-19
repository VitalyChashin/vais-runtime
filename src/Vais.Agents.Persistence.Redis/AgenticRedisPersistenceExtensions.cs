// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.Extensions.Hosting;
using StackExchange.Redis;
using Vais.Agents.Hosting.Orleans;

namespace Vais.Agents.Persistence.Redis;

/// <summary>
/// Silo- and client-side DI extensions that wire Orleans' built-in Redis providers
/// into the conventions used by <see cref="AiAgentGrain"/>. Two behaviours consumers
/// can opt into independently: clustering (silo membership table in Redis) and grain
/// storage (grain state in Redis, under <see cref="AiAgentGrain.StorageName"/>).
/// </summary>
/// <remarks>
/// <para>
/// Three behaviours consumers can opt into independently: clustering, grain storage,
/// and streaming. Streaming pairs with <see cref="OrleansAgentEventBus"/> to back a
/// cross-silo <see cref="IAgentEventBus"/> over Redis.
/// </para>
/// <para>
/// Every extension accepts a connection string in <c>StackExchange.Redis</c> format
/// (e.g. <c>"localhost:6379"</c>, <c>"my-cache.redis.cache.windows.net:6380,ssl=true,password=..."</c>).
/// The string is parsed once into <see cref="ConfigurationOptions"/>.
/// </para>
/// <para>
/// <b>Streaming.Redis is alpha.</b> The streaming extensions here depend on
/// <c>Microsoft.Orleans.Streaming.Redis 10.1.0-alpha.1</c>. No stable release is
/// published yet. For production, pair with a stream provider whose stability matches
/// your deployment risk profile — <see cref="OrleansAgentEventBus"/> is provider-neutral
/// by design, so swapping to memory streams or EventHubs is a wiring change only.
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

    /// <summary>
    /// Register a Redis-backed Orleans stream provider under
    /// <see cref="OrleansAgentEventBus.StreamNamespace"/>. Pair with
    /// <see cref="OrleansAgentEventBus"/> to back a cross-silo
    /// <see cref="IAgentEventBus"/> over Redis streams.
    /// </summary>
    /// <remarks>
    /// Depends on <c>Microsoft.Orleans.Streaming.Redis</c>, which at the time this extension
    /// was introduced had only an alpha release (<c>10.1.0-alpha.1</c>). For production
    /// workloads, evaluate the alpha risk against alternative stream providers
    /// (memory streams for single-process, EventHubs for Azure). Consumers that want a
    /// different provider name should call <c>AddRedisStreams(name, ...)</c> directly
    /// from Orleans and register their <see cref="OrleansAgentEventBus"/> against it.
    /// </remarks>
    public static ISiloBuilder UseAgenticRedisStreaming(
        this ISiloBuilder siloBuilder,
        string connectionString)
    {
        ArgumentNullException.ThrowIfNull(siloBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        var options = ConfigurationOptions.Parse(connectionString);
        return siloBuilder.AddRedisStreams(
            OrleansAgentEventBus.StreamNamespace,
            configurator => configurator.ConfigureOptions((opts, _) => opts.ConfigurationOptions = options));
    }

    /// <summary>
    /// Register a Redis-backed Orleans stream provider on a client under
    /// <see cref="OrleansAgentEventBus.StreamNamespace"/>. Mirrors the silo-side
    /// <see cref="UseAgenticRedisStreaming(ISiloBuilder, string)"/>.
    /// </summary>
    public static IClientBuilder UseAgenticRedisStreaming(
        this IClientBuilder clientBuilder,
        string connectionString)
    {
        ArgumentNullException.ThrowIfNull(clientBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        var options = ConfigurationOptions.Parse(connectionString);
        return clientBuilder.AddRedisStreams(
            OrleansAgentEventBus.StreamNamespace,
            configurator => configurator.ConfigureOptions((opts, _) => opts.ConfigurationOptions = options));
    }
}
