// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Orleans.Hosting;
using Vais.Agents.Control;
using Vais.Agents.Control.Http;
using Vais.Agents.Control.InProcess;
using Vais.Agents.Control.Policy.Opa;
using Vais.Agents.Core;
using Vais.Agents.Hosting.Orleans;
using Vais.Agents.Observability.Langfuse;
using Vais.Agents.Observability.OpenTelemetry;
using Vais.Agents.Persistence.Postgres;
using Vais.Agents.Persistence.Redis;
using Vais.Agents.Runtime.Instantiation;
using Vais.Agents.Runtime.Instantiation.Guardrails;
using Vais.Agents.Runtime.Instantiation.ModelProviders;
using Vais.Agents.Runtime.Plugins;

namespace Vais.Agents.Runtime.Host;

/// <summary>
/// Composition-root helpers for the runtime host. Split from <c>Program.cs</c> so that
/// unit tests in <c>Vais.Agents.Runtime.Host.Tests</c> can drive the same wiring against
/// a vanilla <see cref="IServiceCollection"/> without booting Orleans end-to-end.
/// </summary>
/// <remarks>
/// <para>
/// The public shape is two methods: <see cref="ConfigureSilo(ISiloBuilder, RuntimeOptions)"/>
/// runs inside the <c>UseOrleans</c> callback and wires clustering + grain storage + streams;
/// <see cref="ConfigureServices(IServiceCollection, RuntimeOptions)"/> runs against the
/// top-level <c>IServiceCollection</c> and wires durability sidecars, the HTTP control plane,
/// optional observability, and optional OPA.
/// </para>
/// <para>
/// <b>Ordering discipline.</b> The three durability sidecars
/// (<c>OrleansTaskStore</c> / <c>OrleansCheckpointer</c> / <c>OrleansIdempotencyStore</c>) all
/// use <c>TryAddSingleton</c> and must be registered <em>before</em> the generic control-plane
/// wiring — otherwise the in-memory defaults in <c>AddAgentControlPlaneIdempotency</c> etc. win
/// silently. <see cref="ConfigureServices"/> encodes the correct order; the composition-root
/// unit tests lock it in.
/// </para>
/// </remarks>
internal static class CompositionRoot
{
    /// <summary>
    /// Wire the Orleans silo: clustering + grain storage + stream provider. Called from
    /// <c>builder.Host.UseOrleans(silo =&gt; CompositionRoot.ConfigureSilo(silo, options))</c>.
    /// </summary>
    public static void ConfigureSilo(ISiloBuilder silo, RuntimeOptions options)
    {
        ArgumentNullException.ThrowIfNull(silo);
        ArgumentNullException.ThrowIfNull(options);
        options.EnsureValid();

        if (options.Mode == "localhost")
        {
            silo.UseLocalhostClustering();
            silo.AddMemoryGrainStorage("Default");
            silo.AddMemoryStreams(OrleansAgentEventBus.StreamNamespace);
            return;
        }

        // clustered
        if (options.ClusteringBackend == "redis")
        {
            var conn = options.RedisConnection!;
            silo.UseAgenticRedisClustering(conn);
            silo.AddAgenticRedisGrainStorage(conn);
            silo.UseAgenticRedisStreaming(conn);
        }
        else // postgres
        {
            var conn = options.PostgresConnection!;
            silo.UseAgenticPostgresClustering(conn);
            silo.AddAgenticPostgresGrainStorage(conn);
            // Orleans 10.x has no production-grade Postgres stream provider
            // (Streaming.AdoNet is not shipped), so we fall back to in-silo
            // memory streams. OrleansAgentEventBus still works but does not
            // fan out across silos. Documented in install-the-runtime-locally.md.
            silo.AddMemoryStreams(OrleansAgentEventBus.StreamNamespace);
        }
    }

    /// <summary>
    /// Wire the top-level <see cref="IServiceCollection"/>. Caller is expected to have
    /// already set up Orleans via <c>builder.Host.UseOrleans(...)</c>; this method only
    /// registers services that the ASP.NET Core host owns.
    /// </summary>
    public static void ConfigureServices(IServiceCollection services, RuntimeOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        options.EnsureValid();

        // 1. Orleans durability sidecars — FIRST, before any control-plane wiring. All three
        //    use TryAddSingleton; once AddAgentControlPlaneIdempotency runs with its InMemory
        //    default, it is too late. See v0.11 findings for the ordering footgun.
        services.AddOrleansA2ATaskStore();
        services.AddOrleansGraphCheckpointer();
        services.AddOrleansIdempotencyStore();

        // 2. Orleans-backed agent runtime (client-side) + stream-backed event bus. These
        //    bind IAgentRuntime + IAgentContextAccessor + IAgentEventBus against IGrainFactory,
        //    which the co-hosted silo exposes into the same DI container.
        services.AddOrleansAgentRuntime();
        services.AddOrleansAgentEventBus();

        // 3. v0.17 Pillar B — swap InMemory registry for Orleans-backed (survives pod roll),
        //    register the manifest translator + built-in model providers + built-in
        //    guardrails, and point ConfigureAgentGrains at the translator so grain
        //    activation yields options produced from the stored manifest.
        //
        //    Ordering discipline (locked by Composition_Translator_Registered_Before_ConfigureAgentGrains):
        //    all three Add*Instantiator / Add*Providers / Add*Guardrails calls must
        //    precede ConfigureAgentGrains. The lambda closes over sp.GetRequiredService
        //    so the translator must be registered before the Func<string, options>
        //    gets resolved at grain activation.
        services.AddOrleansAgentRegistry();
        services.AddOrleansAgentGraphRegistry();
        services.TryAddSingleton<ISecretResolver>(_ => CompositeSecretResolver.CreateDefault());

        // v0.18 Pillar C — plugin loader. Must register BEFORE AddAgentManifestInstantiator
        // because the translator constructor calls sp.GetService<IPluginHandlerRegistry>()
        // lazily at build time; an absent registry → translator falls through to the v0.17
        // declarative path. Empty / whitespace PluginsDirectory → skip wiring entirely
        // (disabled mode). Non-existent directory is handled by the loader itself as a no-op
        // with an empty registry.
        if (!string.IsNullOrWhiteSpace(options.PluginsDirectory))
        {
            services.AddAgentPlugins(options.PluginsDirectory);
        }

        services.AddAgentManifestInstantiator();
        services.AddBuiltinModelProviders();
        services.AddBuiltinGuardrails();

        services.ConfigureAgentGrains((sp, id) =>
            sp.GetRequiredService<IAgentManifestTranslator>().TranslateForGrain(sp, id));

        services.AddSingleton<IAuditLog, LoggerAuditLog>();
        services.AddSingleton<IAgentLifecycleManager>(sp => new AgentLifecycleManager(
            sp.GetRequiredService<IAgentRegistry>(),
            sp.GetRequiredService<IAgentRuntime>(),
            policy: sp.GetService<IAgentPolicyEngine>(),
            audit: sp.GetService<IAuditLog>(),
            contextAccessor: sp.GetService<IAgentContextAccessor>(),
            logger: sp.GetService<ILogger<AgentLifecycleManager>>() ?? NullLogger<AgentLifecycleManager>.Instance));
        services.AddSingleton<IAgentGraphLifecycleManager>(sp => new AgentGraphLifecycleManager(
            sp.GetRequiredService<IAgentGraphRegistry>(),
            sp.GetRequiredService<IAgentRegistry>(),
            sp.GetRequiredService<IAgentLifecycleManager>(),
            sp.GetRequiredService<IGraphCheckpointer>(),
            policy: sp.GetService<IAgentPolicyEngine>(),
            audit: sp.GetService<IAuditLog>(),
            contextAccessor: sp.GetService<IAgentContextAccessor>(),
            logger: sp.GetService<ILogger<AgentGraphLifecycleManager>>() ?? NullLogger<AgentGraphLifecycleManager>.Instance));

        // 4. HTTP control plane (routes, idempotency middleware, OpenAPI doc).
        services.AddAgentControlPlane();
        services.AddAgentControlPlaneIdempotency();
        services.AddAgentControlPlaneOpenApi();

        // 5. Optional observability. Off unless either the OTel endpoint env var or the
        //    console-exporter toggle is set; off-by-default keeps hello-world overhead zero.
        ConfigureObservability(services, options);

        // 6. Optional OPA policy engine. Off-by-default → NullAgentPolicyEngine.Instance
        //    (AllowAll) wins. AddOpaPolicyEngine uses TryAddSingleton<IAgentPolicyEngine>,
        //    so it only binds when no prior registration exists — the startup log records
        //    which engine is active so the default-open behaviour is never silent.
        if (!string.IsNullOrWhiteSpace(options.OpaBaseUrl))
        {
            services.AddOpaPolicyEngine(o =>
            {
                o.BaseUrl = new Uri(options.OpaBaseUrl);
                o.FailMode = options.OpaFailMode;
                if (!string.IsNullOrWhiteSpace(options.OpaDataPath))
                {
                    o.DataPath = options.OpaDataPath;
                }
            });
        }

        // 7. Health checks. Liveness (/healthz) maps to the default "catch-all" set; readiness
        //    (/readyz) filters on the "ready" tag and gates on the co-hosted silo reaching
        //    SiloStatus.Active. Probe tuning lives in the Helm chart (60s failure threshold).
        services.AddHealthChecks()
            .AddCheck<OrleansActiveHealthCheck>("orleans", tags: ["ready"]);
    }

    private static void ConfigureObservability(IServiceCollection services, RuntimeOptions options)
    {
        var otelEnabled = !string.IsNullOrWhiteSpace(options.OtelEndpoint) || options.OtelConsole;
        if (otelEnabled)
        {
            services.AddOpenTelemetry()
                .WithTracing(t =>
                {
                    t.AddAgenticInstrumentation();
                    if (!string.IsNullOrWhiteSpace(options.OtelEndpoint))
                    {
                        t.AddOtlpExporter(o => o.Endpoint = new Uri(options.OtelEndpoint));
                    }
                    if (options.OtelConsole)
                    {
                        t.AddConsoleExporter();
                    }
                })
                .WithMetrics(m =>
                {
                    m.AddAgenticInstrumentation();
                    if (!string.IsNullOrWhiteSpace(options.OtelEndpoint))
                    {
                        m.AddOtlpExporter(o => o.Endpoint = new Uri(options.OtelEndpoint));
                    }
                    if (options.OtelConsole)
                    {
                        m.AddConsoleExporter();
                    }
                });

            services.AddAgenticOpenTelemetrySink();
        }

        if (!string.IsNullOrWhiteSpace(options.LangfuseProject))
        {
            services.AddLangfuseEnrichment(new LangfuseEnrichmentOptions
            {
                StaticMetadata = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["project"] = options.LangfuseProject,
                },
            });
        }
    }
}
