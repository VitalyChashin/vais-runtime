// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using A2A;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Vais.Agents.Core;
using Vais.Agents.Runtime.Plugins;

namespace Vais.Agents.Hosting.Orleans;

/// <summary>
/// DI extension methods for wiring the Orleans-backed agent runtime on the client side,
/// and for registering agent-grain dependencies on the silo side.
/// </summary>
public static class AgenticHostingOrleansServiceCollectionExtensions
{
    /// <summary>
    /// Register <see cref="OrleansAgentRuntime"/> as a singleton <see cref="IAgentRuntime"/>
    /// plus an <see cref="OrleansAgentContextAccessor"/> that reads Orleans
    /// <c>RequestContext</c>. Call this on the <c>IServiceCollection</c> of the
    /// Orleans <em>client</em> (or combined silo+client host).
    /// </summary>
    /// <remarks>
    /// Silo-side grain dependencies (<see cref="ICompletionProvider"/>,
    /// <see cref="Func{String, StatefulAgentOptions}"/>) must additionally be registered
    /// on the silo via <see cref="ConfigureAgentGrains"/>.
    /// </remarks>
    /// <param name="services">The host's DI container.</param>
    /// <returns>The same <paramref name="services"/> instance for chaining.</returns>
    public static IServiceCollection AddOrleansAgentRuntime(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<OrleansAgentContextAccessor>();
        services.TryAddSingleton<IAgentContextAccessor, OrleansAgentContextAccessor>();
        services.TryAddSingleton<IAgentContextSetter, AsyncLocalAgentContextAccessor>();
        services.TryAddSingleton<IAgentRuntime>(sp => new OrleansAgentRuntime(sp.GetRequiredService<IGrainFactory>()));
        return services;
    }

    /// <summary>
    /// Register <see cref="OrleansAgentEventBus"/> as a singleton <see cref="IAgentEventBus"/>
    /// backed by an Orleans stream provider. The stream provider itself must already be
    /// configured on the host (e.g. <c>siloBuilder.AddMemoryStreams(name)</c>,
    /// <c>AddEventHubStreams(name, ...)</c>, etc.); this method wires the bus that publishes
    /// and subscribes against it.
    /// </summary>
    /// <param name="services">The host's DI container.</param>
    /// <param name="streamProviderName">
    /// Name of the already-registered Orleans stream provider. Defaults to
    /// <see cref="OrleansAgentEventBus.StreamNamespace"/>.
    /// </param>
    public static IServiceCollection AddOrleansAgentEventBus(
        this IServiceCollection services,
        string streamProviderName = OrleansAgentEventBus.StreamNamespace)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(streamProviderName);

        services.TryAddSingleton<IAgentEventBus>(sp =>
            new OrleansAgentEventBus(sp.GetRequiredService<IClusterClient>(), streamProviderName));
        return services;
    }

    /// <summary>
    /// Register <see cref="OrleansAgentGraphEventBus"/> as a singleton <see cref="IAgentGraphEventBus"/>
    /// backed by an Orleans stream provider — the graph-scoped sibling of
    /// <see cref="AddOrleansAgentEventBus"/>. The stream provider must already be configured on the
    /// host (e.g. <c>siloBuilder.AddMemoryStreams(name)</c>); this wires the bus that publishes and
    /// subscribes against it.
    /// </summary>
    /// <param name="services">The host's DI container.</param>
    /// <param name="streamProviderName">
    /// Name of the already-registered Orleans stream provider. Defaults to
    /// <see cref="OrleansAgentEventBus.StreamNamespace"/> — the provider both buses share.
    /// </param>
    public static IServiceCollection AddOrleansAgentGraphEventBus(
        this IServiceCollection services,
        string streamProviderName = OrleansAgentEventBus.StreamNamespace)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(streamProviderName);

        services.TryAddSingleton<IAgentGraphEventBus>(sp =>
            new OrleansAgentGraphEventBus(sp.GetRequiredService<IClusterClient>(), streamProviderName));
        return services;
    }

    /// <summary>
    /// Register <see cref="OrleansGraphRunCoordinator"/> as the singleton
    /// <see cref="Vais.Agents.Control.IGraphRunCoordinator"/>, making graph-run conflict detection,
    /// cancellation, and status reachable from any silo (P1). Graph-run sibling of the background
    /// agent tracker.
    /// </summary>
    public static IServiceCollection AddOrleansGraphRunCoordinator(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<Vais.Agents.Control.IGraphRunCoordinator>(sp =>
            new OrleansGraphRunCoordinator(sp.GetRequiredService<IGrainFactory>()));
        return services;
    }

    /// <summary>
    /// Register <see cref="OrleansInvokeLeaseStore"/> as the singleton
    /// <see cref="Vais.Agents.Core.IInvokeLeaseStore"/>, making session-mode call-token liveness
    /// reachable from any silo (P1). Register before the in-process/in-memory fallback so it wins in a
    /// clustered runtime. The call-token sibling of <see cref="AddOrleansGraphRunCoordinator"/>.
    /// </summary>
    public static IServiceCollection AddOrleansInvokeLeaseStore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<Vais.Agents.Core.IInvokeLeaseStore>(sp =>
            new OrleansInvokeLeaseStore(sp.GetRequiredService<IGrainFactory>()));
        return services;
    }

    /// <summary>
    /// Register the silo-side dependencies required by <see cref="AiAgentGrain"/>:
    /// a <see cref="Func{String, CancellationToken, ValueTask}"/> that produces per-agent options.
    /// Expects <see cref="ICompletionProvider"/> to be registered separately (consumers
    /// choose between the SK and MAF adapter packages).
    /// </summary>
    /// <param name="services">The silo's DI container.</param>
    /// <param name="configureAgents">
    /// Optional. Given an agent id and cancellation token, returns the
    /// <see cref="StatefulAgentOptions"/> for that agent. Default: a plain options instance
    /// with <see cref="StatefulAgentOptions.AgentName"/> set to the id.
    /// </param>
    public static IServiceCollection ConfigureAgentGrains(
        this IServiceCollection services,
        Func<IServiceProvider, string, CancellationToken, ValueTask<StatefulAgentOptions>>? configureAgents = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<Func<string, CancellationToken, ValueTask<StatefulAgentOptions>>>(sp =>
            configureAgents is null
                ? (id, _) => ValueTask.FromResult(new StatefulAgentOptions { AgentName = id })
                : (id, ct) => configureAgents(sp, id, ct));
        return services;
    }

    /// <summary>
    /// Register <see cref="OrleansTaskStore"/> as the <see cref="ITaskStore"/> for the
    /// A2A inbound server. Survives silo restart — input-required tasks can be resumed
    /// days or weeks later without losing the interrupt envelope.
    /// </summary>
    /// <remarks>
    /// Call this <em>before</em> <c>AddA2AAgentServer</c> so the <c>TryAddSingleton</c>
    /// in that extension skips its default <c>InMemoryTaskStore</c> registration.
    /// Requires an <see cref="IGrainFactory"/> in the DI container (provided by the
    /// Orleans client/silo).
    /// </remarks>
    /// <param name="services">The host's DI container.</param>
    public static IServiceCollection AddOrleansA2ATaskStore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<ITaskStore>(sp => new OrleansTaskStore(sp.GetRequiredService<IGrainFactory>()));
        return services;
    }

    /// <summary>
    /// Register <see cref="OrleansCheckpointer"/> as the <see cref="IGraphCheckpointer"/>
    /// for the v0.9 graph orchestrator. Interrupted graphs survive silo restart — a
    /// <c>GraphInterrupted</c> pause persisted via this checkpointer can be resumed
    /// days or weeks later without losing graph state.
    /// </summary>
    /// <remarks>
    /// Call this <em>before</em> constructing an <c>InProcessGraphOrchestrator</c>
    /// (or any other <see cref="IAgentGraph{TState}"/> impl) so the orchestrator
    /// picks up the durable checkpointer from DI rather than the in-memory default.
    /// Requires an <see cref="IGrainFactory"/> in the DI container (provided by the
    /// Orleans client/silo).
    /// </remarks>
    /// <param name="services">The host's DI container.</param>
    public static IServiceCollection AddOrleansGraphCheckpointer(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IGraphCheckpointer>(sp => new OrleansCheckpointer(sp.GetRequiredService<IGrainFactory>()));
        return services;
    }

    /// <summary>
    /// Register <see cref="OrleansIdempotencyStore"/> as the durable
    /// <see cref="Vais.Agents.Control.IIdempotencyStore"/>. Call <b>before</b>
    /// <c>AddAgentControlPlaneIdempotency</c> so the <c>TryAddSingleton</c>
    /// discipline picks this Orleans implementation over the InMemory default.
    /// </summary>
    /// <param name="services">The host's DI container.</param>
    /// <param name="ttl">Optional TTL override. Pass the same value as the HTTP
    /// middleware's <c>IdempotencyOptions.Ttl</c> so both sides agree on when
    /// entries expire. Null defaults to <see cref="OrleansIdempotencyStore.DefaultTtl"/> (24h).</param>
    public static IServiceCollection AddOrleansIdempotencyStore(this IServiceCollection services, TimeSpan? ttl = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<Vais.Agents.Control.IIdempotencyStore>(sp => new OrleansIdempotencyStore(
            sp.GetRequiredService<IGrainFactory>(),
            ttl ?? OrleansIdempotencyStore.DefaultTtl));
        return services;
    }

    /// <summary>
    /// Register <see cref="OrleansAgentRegistry"/> as the cluster-backed
    /// <see cref="IAgentRegistry"/>. Manifests survive silo restart via the
    /// configured grain-storage provider (memory / Redis / Postgres).
    /// </summary>
    /// <remarks>
    /// The v0.17 Pillar B runtime host swaps <c>InMemoryAgentRegistry</c> for
    /// this registration so <c>vais apply -f</c> actually persists across
    /// pod roll. Both the concrete <see cref="OrleansAgentRegistry"/> (for
    /// callers that need <c>RegisterAsync</c> / <c>RemoveAsync</c>) and the
    /// <see cref="IAgentRegistry"/> interface land in DI so
    /// <c>AgentLifecycleManager.CreateAsync</c> resolves the concrete type.
    /// </remarks>
    /// <param name="services">The host's DI container.</param>
    public static IServiceCollection AddOrleansAgentRegistry(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(sp => new OrleansAgentRegistry(sp.GetRequiredService<IGrainFactory>()));
        services.TryAddSingleton<IAgentRegistry>(sp => sp.GetRequiredService<OrleansAgentRegistry>());
        return services;
    }

    /// <summary>
    /// Register <see cref="OrleansAgentGraphRegistry"/> as the durable
    /// <see cref="IAgentGraphRegistry"/>. Graph manifest registrations survive
    /// silo restart via the configured grain-storage provider.
    /// Both the concrete type (for mutation callers) and the interface land in DI.
    /// </summary>
    /// <param name="services">The host's DI container.</param>
    public static IServiceCollection AddOrleansAgentGraphRegistry(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(sp => new OrleansAgentGraphRegistry(sp.GetRequiredService<IGrainFactory>()));
        services.TryAddSingleton<IAgentGraphRegistry>(sp => sp.GetRequiredService<OrleansAgentGraphRegistry>());
        return services;
    }

    /// <summary>
    /// Register <see cref="OrleansLlmGatewayConfigRegistry"/> as the durable
    /// <see cref="ILlmGatewayConfigRegistry"/>. Config registrations survive silo restart
    /// via the configured grain-storage provider.
    /// </summary>
    /// <param name="services">The host's DI container.</param>
    public static IServiceCollection AddOrleansLlmGatewayConfigRegistry(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(sp => new OrleansLlmGatewayConfigRegistry(sp.GetRequiredService<IGrainFactory>()));
        services.TryAddSingleton<ILlmGatewayConfigRegistry>(sp => sp.GetRequiredService<OrleansLlmGatewayConfigRegistry>());
        return services;
    }

    /// <summary>
    /// Register <see cref="OrleansMcpGatewayConfigRegistry"/> as the durable
    /// <see cref="IMcpGatewayConfigRegistry"/>. Config registrations survive silo restart
    /// via the configured grain-storage provider.
    /// </summary>
    /// <param name="services">The host's DI container.</param>
    public static IServiceCollection AddOrleansMcpGatewayConfigRegistry(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(sp => new OrleansMcpGatewayConfigRegistry(sp.GetRequiredService<IGrainFactory>()));
        services.TryAddSingleton<IMcpGatewayConfigRegistry>(sp => sp.GetRequiredService<OrleansMcpGatewayConfigRegistry>());
        return services;
    }

    /// <summary>
    /// Register <see cref="OrleansMcpServerRegistry"/> as the durable
    /// <see cref="IMcpServerRegistry"/>. Server registrations survive silo restart
    /// via the configured grain-storage provider.
    /// </summary>
    /// <param name="services">The host's DI container.</param>
    public static IServiceCollection AddOrleansMcpServerRegistry(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(sp => new OrleansMcpServerRegistry(sp.GetRequiredService<IGrainFactory>()));
        services.TryAddSingleton<IMcpServerRegistry>(sp => sp.GetRequiredService<OrleansMcpServerRegistry>());
        return services;
    }

    /// <summary>
    /// Register <see cref="OrleansContainerPluginRegistry"/> as the durable
    /// <see cref="IContainerPluginRegistry"/>. Plugin registrations survive silo restart
    /// via the configured grain-storage provider.
    /// </summary>
    /// <param name="services">The host's DI container.</param>
    public static IServiceCollection AddOrleansContainerPluginRegistry(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(sp => new OrleansContainerPluginRegistry(sp.GetRequiredService<IGrainFactory>()));
        services.TryAddSingleton<IContainerPluginRegistry>(sp => sp.GetRequiredService<OrleansContainerPluginRegistry>());
        return services;
    }

    /// <summary>
    /// Register <see cref="OrleansEvalSuiteRegistry"/> as the durable eval suite registry.
    /// Suite registrations survive silo restart via the configured grain-storage provider.
    /// </summary>
    /// <param name="services">The host's DI container.</param>
    public static IServiceCollection AddOrleansEvalSuiteRegistry(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(sp => new OrleansEvalSuiteRegistry(sp.GetRequiredService<IGrainFactory>()));
        services.TryAddSingleton<IEvalSuiteRegistry>(sp => sp.GetRequiredService<OrleansEvalSuiteRegistry>());
        return services;
    }

    /// <summary>
    /// Register <see cref="GrainReactivationOnPluginReloadHook"/> as an <see cref="IPluginReloadHook"/>.
    /// When a plugin hot-reload swaps the handler registry, this hook deactivates affected
    /// agent grains so the next invocation reactivates against the new plugin code.
    /// Must be called after <c>AddAgentManifestInstantiator</c> (which registers the
    /// <c>TranslatorInvalidationHook</c> at Order 0; this hook runs at Order 100).
    /// </summary>
    /// <param name="services">The host's DI container.</param>
    public static IServiceCollection AddGrainReactivationPluginReloadHook(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IPluginReloadHook>(sp =>
            new GrainReactivationOnPluginReloadHook(
                sp.GetRequiredService<IAgentRegistry>(),
                sp.GetRequiredService<IGrainFactory>(),
                sp.GetService<ILogger<GrainReactivationOnPluginReloadHook>>()));
        return services;
    }

    /// <summary>
    /// Register <see cref="OrleansEvalRunLifecycleManager"/> as the <see cref="Vais.Agents.Eval.IEvalRunLifecycleManager"/>.
    /// Each eval run is backed by an <see cref="IEvalRunGrain"/> (one grain per run id).
    /// </summary>
    /// <param name="services">The host's DI container.</param>
    public static IServiceCollection AddOrleansEvalRunLifecycleManager(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<OrleansEvalRunLifecycleManager>();
        services.TryAddSingleton<Vais.Agents.Eval.IEvalRunLifecycleManager>(
            sp => sp.GetRequiredService<OrleansEvalRunLifecycleManager>());
        return services;
    }

    /// <summary>
    /// Register the continuous eval scoring pipeline:
    /// <list type="bullet">
    ///   <item><see cref="RunCompletionEventBusBridge"/> — bridges <see cref="IAgentEventBus"/> events to <see cref="IRunCompletionListener"/>s.</item>
    ///   <item><see cref="ProductionSampler"/> — deterministic-hash sampler that enqueues matched runs to <see cref="IContinuousScoringGrain"/>.</item>
    ///   <item><see cref="ContinuousSuiteActivator"/> — refreshes the in-memory suite index from the registry every 30 s.</item>
    /// </list>
    /// Requires <see cref="IAgentEventBus"/>, <c>IEvalSuiteRegistry</c>,
    /// <c>IContinuousSuiteIndex</c>, and <see cref="IGrainFactory"/> in the container.
    /// </summary>
    /// <param name="services">The host's DI container.</param>
    public static IServiceCollection AddContinuousEvalScoring(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IRunCompletionListener, ProductionSampler>());
        services.AddHostedService<RunCompletionEventBusBridge>();
        services.AddHostedService<ContinuousSuiteActivator>();
        return services;
    }
}
