// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Vais.Agents.Runtime.Host.Diagnostics;
using Orleans.Configuration;
using Orleans.Hosting;
using Vais.Agents;
using Vais.Agents.Control;
using Vais.Agents.Control.Http;
using Vais.Agents.Control.InProcess;
using Vais.Agents.Control.Mcp;
using Vais.Agents.Control.Policy.Opa;
using Vais.Agents.Core;
using Vais.Agents.Core.PowerFx;
using Vais.Agents.Hosting.Orleans;
using Vais.Agents.Protocols.A2A;
using Vais.Agents.Hosting.InMemory;
using Vais.Agents.Observability.AgentLogs;
using Vais.Agents.Observability.AgentRunStore;
using Vais.Agents.Observability.GatewayEventStore;
using Vais.Agents.Observability.Langfuse;
using Vais.Agents.Observability.McpEventStore;
using Vais.Agents.Observability.McpGatewayEventStore;
using Vais.Agents.Observability.OpenTelemetry;
using Vais.Agents.Observability.RunStore;
using Vais.Agents.Persistence.Postgres;
using Vais.Agents.Persistence.Redis;
using Vais.Agents.Runtime.Instantiation;
using Vais.Agents.Runtime.Instantiation.Guardrails;
using Vais.Agents.Runtime.Instantiation.ModelProviders;
using Vais.Agents.Runtime.Plugins;
using Vais.Agents.Runtime.Plugins.Python;
using Vais.Agents.Runtime.Plugins.Container;
using Vais.Agents.Gateways.Prometheus;
using Vais.Agents.Gateways.Fallback;
using Vais.Agents.Gateways.SemanticCache;
using Vais.Agents.Gateways.StructuredOutput;
using Vais.Agents.Gateways.McpGovernance;
using Vais.Agents.Gateways.McpSecurity;
using Vais.Agents.Gateways.McpReliability;
using Vais.Agents.Gateways.McpCache;
using System.Text.Json;
using Vais.Agents.Gateways.McpTransformation;
using Vais.Agents.Orchestration.Graph.MicrosoftAgentFramework;

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

        // Propagate Activity trace context into grain calls so grain.ask spans are
        // parented to the caller's graph.node span in Langfuse.
        silo.AddOrleansActivityPropagation();

        if (options.Mode == "localhost")
        {
            silo.UseLocalhostClustering();

            // "Default" is a safety net for any grain that omits a storage-name in
            // [PersistentState]; no current grain uses it. Always in-memory.
            silo.AddMemoryGrainStorage("Default");

            // Primary grain store — registry, agents, checkpoints, idempotency, sessions.
            if (options.LocalhostPersistence == LocalhostPersistenceMode.Postgres)
                silo.AddAgenticPostgresGrainStorage(options.PostgresConnection!);
            else
                silo.AddMemoryGrainStorage(AiAgentGrain.StorageName);

            // PubSubStore — required by AddMemoryStreams pub-sub.
            if (options.LocalhostPubSubPersistence == LocalhostPersistenceMode.Postgres)
                silo.AddAgenticPostgresGrainStorage("PubSubStore", options.PostgresConnection!);
            else
                silo.AddMemoryGrainStorage("PubSubStore");

            silo.AddMemoryStreams(OrleansAgentEventBus.StreamNamespace);
            // LLM-backed grains (plan + summarize) routinely exceed the 30-second default.
            silo.Configure<SiloMessagingOptions>(o => o.ResponseTimeout = TimeSpan.FromMinutes(2));
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
            silo.AddMemoryGrainStorage("PubSubStore"); // required by AddMemoryStreams pub-sub
            silo.AddMemoryStreams(OrleansAgentEventBus.StreamNamespace);
        }
    }

    /// <summary>
    /// Wire the top-level <see cref="IServiceCollection"/>. Caller is expected to have
    /// already set up Orleans via <c>builder.Host.UseOrleans(...)</c>; this method only
    /// registers services that the ASP.NET Core host owns.
    /// </summary>
    public static void ConfigureServices(IServiceCollection services, RuntimeOptions options, IConfiguration? configuration = null)
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

        // IHttpClientFactory — required by plugin handlers that take IHttpClientFactory in
        // their constructor. AddHttpClient is idempotent; calling it here makes it available
        // in the plugin DI container without the plugin author having to register it manually.
        services.AddHttpClient();

        // 2. Orleans-backed agent runtime (client-side) + stream-backed event bus. These
        //    bind IAgentRuntime + IAgentContextAccessor + IAgentEventBus against IGrainFactory,
        //    which the co-hosted silo exposes into the same DI container.
        services.AddOrleansAgentRuntime();
        services.AddOrleansAgentEventBus();

        // IAgentGraphEventBus — in-process fan-out bus shared by AgentGraphLifecycleManager
        // (publishes events) and RunStoreSubscriber (persists them). Singleton so all components
        // share the same instance.
        services.AddSingleton<IAgentGraphEventBus, InMemoryAgentGraphEventBus>();

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
        services.AddOrleansLlmGatewayConfigRegistry();
        services.AddOrleansMcpGatewayConfigRegistry();
        services.AddOrleansMcpServerRegistry();
        services.AddOrleansContainerPluginRegistry();
        services.TryAddSingleton<ISecretResolver>(_ => CompositeSecretResolver.CreateDefault());

        // v0.18 Pillar C — plugin loader. Must register BEFORE AddAgentManifestInstantiator
        // because the translator constructor calls sp.GetService<IPluginHandlerRegistry>()
        // lazily at build time; an absent registry → translator falls through to the v0.17
        // declarative path. Empty / whitespace PluginsDirectory → skip wiring entirely
        // (disabled mode). Non-existent directory is handled by the loader itself as a no-op
        // with an empty registry.
        if (!string.IsNullOrWhiteSpace(options.PluginsDirectory))
        {
            services.AddAgentPlugins(
                options.PluginsDirectory,
                new PluginLoaderOptions { ReloadPolicy = options.PluginsHotReload });
        }

        // v0.23 Python-plugins pillar — opt-in via VAIS_PYTHON_PLUGINS_DIRECTORY. Must register
        // before AddAgentManifestInstantiator so INamedToolSourceProvider reaches the translator.
        if (!string.IsNullOrWhiteSpace(options.PythonPluginsDirectory))
        {
            services.AddContainerGatewayCallToken();
            services.AddPythonPlugins(new PythonPluginLoaderOptions
            {
                PluginsDirectory = options.PythonPluginsDirectory,
                // In localhost mode, run `uv sync --frozen` automatically when .venv/ is absent
                // so contributors don't need a manual setup step after cloning.
                FallbackUvSync = options.Mode == "localhost",
                ReloadPolicy = options.PythonPluginsReloadPolicy,
                InternalGatewayBaseUrl = options.InternalGatewayBaseUrl,
            });
            services.AddHealthChecks()
                .AddCheck<PythonPluginsReadyCheck>("python-plugins", tags: ["ready"]);
        }

        // Container-plugins pillar — opt-in via VAIS_CONTAINER_PLUGINS_DIRECTORY.
        if (!string.IsNullOrWhiteSpace(options.ContainerPluginsDirectory))
        {
            services.AddContainerPlugins(o =>
            {
                o.PluginsDirectory = options.ContainerPluginsDirectory;
                o.PluginNetwork    = options.DockerPluginNetwork;
            });
        }

        // GCF-20/21 — named middleware registrations + composite factories.
        // Each AddNamed* call registers one NamedL*GatewayMiddlewareRegistration singleton;
        // the DefaultL*GatewayMiddlewareFactory collects them all via IEnumerable<> injection.
        // Core LLM middleware
        services.AddNamedLlmGatewayMiddleware_LlmLogging();
        services.AddNamedLlmGatewayMiddleware_LlmUsage();
        services.AddNamedLlmGatewayMiddleware_LlmOtel();
        services.AddNamedLlmGatewayMiddleware_LlmPromptEnrichment();
        // Package LLM middleware
        services.AddNamedLlmGatewayMiddleware_Prometheus();
        services.AddNamedLlmGatewayMiddleware_Fallback();
        services.AddNamedLlmGatewayMiddleware_SemanticCache();
        services.AddNamedLlmGatewayMiddleware_StructuredOutput();
        // Core Tool middleware
        services.AddNamedToolGatewayMiddleware_ToolLogging();
        services.AddNamedToolGatewayMiddleware_ToolOtel();
        services.AddNamedToolGatewayMiddleware_ToolDenyFilter();
        services.AddNamedToolGatewayMiddleware_ToolResponseTruncation();
        // Package Tool middleware
        services.AddNamedToolGatewayMiddleware_ToolRateLimit();
        services.AddNamedToolGatewayMiddleware_ToolWorkspacePolicy();
        services.AddNamedToolGatewayMiddleware_ToolArgumentValidation();
        services.AddNamedToolGatewayMiddleware_ToolOutputLengthGuard();
        services.AddNamedToolGatewayMiddleware_ToolRetry();
        services.AddNamedToolGatewayMiddleware_ToolTimeout();
        services.AddNamedToolGatewayMiddleware_ToolCircuitBreaker();
        services.AddNamedToolGatewayMiddleware_ToolResultCache();
        services.AddNamedToolGatewayMiddleware_ToolJsonRepair();
        services.AddNamedToolGatewayMiddleware_ToolHtmlToMarkdown();
        // Composite factories — resolve named registrations above via IEnumerable<> injection.
        services.AddDefaultLlmGatewayMiddlewareFactory();
        services.AddDefaultToolGatewayMiddlewareFactory();

        services.AddAgentManifestInstantiator();
        services.AddPhysicalMcpServers();
        services.AddBuiltinModelProviders();
        services.AddBuiltinGuardrails();

        services.ConfigureAgentGrains(async (sp, id, ct) =>
            await sp.GetRequiredService<IAgentManifestTranslator>().TranslateForGrain(sp, id, ct).ConfigureAwait(false));

        services.AddSingleton<IAuditLog, LoggerAuditLog>();
        services.AddSingleton<IAgentLifecycleManager>(sp => new AgentLifecycleManager(
            sp.GetRequiredService<IAgentRegistry>(),
            sp.GetRequiredService<IAgentRuntime>(),
            policy: sp.GetService<IAgentPolicyEngine>(),
            audit: sp.GetService<IAuditLog>(),
            contextAccessor: sp.GetService<IAgentContextAccessor>(),
            logger: sp.GetService<ILogger<AgentLifecycleManager>>() ?? NullLogger<AgentLifecycleManager>.Instance));
        // v0.20 Pillar E — cross-runtime graph refs: remote invoker + bearer-token provider.
        // v0.21 — per-runtime identity propagation (Forward / ServiceAccount / TokenExchange).
        // AddHttpContextAccessor is idempotent; AddAgentRemoteInvoker uses TryAddSingleton.
        services.AddHttpContextAccessor();
        var remoteRuntimesSection = configuration?.GetSection("Vais:RemoteRuntimes");
        if (remoteRuntimesSection?.Exists() == true)
        {
            services.AddAgentRemoteInvoker(map =>
            {
                foreach (var child in remoteRuntimesSection.GetChildren())
                {
                    var runtimeOpts = child.Get<RemoteRuntimeOptions>() ?? new RemoteRuntimeOptions();
                    map.Runtimes[child.Key] = runtimeOpts;
                }
            });
        }
        else
        {
            services.AddAgentRemoteInvoker();
        }
        services.AddA2AGraphNodeInvoker();
        services.AddPowerFxExpressionEvaluator();
        services.AddSingleton<IAgentGraphLifecycleManager>(sp =>
        {
            var accessor = sp.GetService<Microsoft.AspNetCore.Http.IHttpContextAccessor>();
            return new AgentGraphLifecycleManager(
                sp.GetRequiredService<IAgentGraphRegistry>(),
                sp.GetRequiredService<IAgentRegistry>(),
                sp.GetRequiredService<IAgentLifecycleManager>(),
                sp.GetRequiredService<IGraphCheckpointer>(),
                policy: sp.GetService<IAgentPolicyEngine>(),
                audit: sp.GetService<IAuditLog>(),
                contextAccessor: sp.GetService<IAgentContextAccessor>(),
                logger: sp.GetService<ILogger<AgentGraphLifecycleManager>>() ?? NullLogger<AgentGraphLifecycleManager>.Instance,
                remoteInvoker: sp.GetService<IAgentRemoteInvoker>(),
                a2aInvoker: sp.GetService<IA2AGraphNodeInvoker>(),
                bearerTokenProvider: () =>
                {
                    var header = accessor?.HttpContext?.Request.Headers.Authorization.ToString();
                    return header?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) == true
                        ? header[7..] : null;
                },
                graphEventBus: sp.GetService<IAgentGraphEventBus>(),
                orchestratorFactory: (manifest, runId) =>
                    new MafGraphOrchestrator<IDictionary<string, JsonElement>>(
                        manifest,
                        sp.GetRequiredService<IAgentRegistry>(),
                        sp.GetRequiredService<IAgentLifecycleManager>(),
                        runIdFactory: () => runId,
                        checkpointer: sp.GetRequiredService<IGraphCheckpointer>(),
                        graphEventBus: sp.GetService<IAgentGraphEventBus>(),
                        remoteInvoker: sp.GetService<IAgentRemoteInvoker>(),
                        a2aInvoker: sp.GetService<IA2AGraphNodeInvoker>(),
                        bearerToken: accessor?.HttpContext?.Request.Headers.Authorization.ToString() is string authHeader
                            && authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                            ? authHeader[7..] : null,
                        expressionEvaluator: sp.GetService<IGraphExpressionEvaluator>()));
        });

        // v0.20 Gateway config lifecycle managers (GCF-17).
        services.AddSingleton<ILlmGatewayConfigLifecycleManager>(sp => new LlmGatewayConfigLifecycleManager(
            sp.GetRequiredService<ILlmGatewayConfigRegistry>(),
            policy: sp.GetService<IAgentPolicyEngine>(),
            audit: sp.GetService<IAuditLog>(),
            contextAccessor: sp.GetService<IAgentContextAccessor>(),
            logger: sp.GetService<ILogger<LlmGatewayConfigLifecycleManager>>()));
        services.AddSingleton<IMcpGatewayConfigLifecycleManager>(sp => new McpGatewayConfigLifecycleManager(
            sp.GetRequiredService<IMcpGatewayConfigRegistry>(),
            policy: sp.GetService<IAgentPolicyEngine>(),
            audit: sp.GetService<IAuditLog>(),
            contextAccessor: sp.GetService<IAgentContextAccessor>(),
            logger: sp.GetService<ILogger<McpGatewayConfigLifecycleManager>>()));
        services.AddSingleton<IMcpServerLifecycleManager>(sp => new McpServerLifecycleManager(
            sp.GetRequiredService<IMcpServerRegistry>(),
            policy: sp.GetService<IAgentPolicyEngine>(),
            audit: sp.GetService<IAuditLog>(),
            contextAccessor: sp.GetService<IAgentContextAccessor>(),
            logger: sp.GetService<ILogger<McpServerLifecycleManager>>()));

        // 4. HTTP control plane (routes, idempotency middleware, OpenAPI doc).
        services.AddAgentControlPlane();
        services.AddAgentControlPlaneIdempotency();
        services.AddAgentControlPlaneOpenApi();

        // 4b. v0.30 JWT authentication + principal mapping. Off-by-default — existing localhost
        //     semantics unchanged. When VAIS_JWT_AUTHORITY is set the full bearer-token pipeline
        //     is wired. VAIS_SA_PRINCIPAL_MAPPER=true must be registered BEFORE
        //     AddAgentControlPlaneJwtAuth so TryAddSingleton<DefaultPrincipalMapper> inside it
        //     sees an existing registration and skips the default.
        if (!string.IsNullOrWhiteSpace(options.JwtAuthority))
        {
            if (options.UseSaPrincipalMapper)
            {
                services.AddSingleton<IPrincipalMapper, ServiceAccountPrincipalMapper>();
            }

            services.AddAgentControlPlaneJwtAuth(o =>
            {
                o.Authority = options.JwtAuthority;
                if (!string.IsNullOrWhiteSpace(options.JwtAudience))
                {
                    o.Audience = options.JwtAudience;
                }
            });
        }

        // 5. Agent log sink — in-memory ring buffer for agent grain and Python subprocess stdout.
        //    Always registered; no connection string required. Buffer cap configurable via
        //    VAIS_AGENT_LOG_BUFFER_LINES (default 500). HTTP endpoint returns entries from sink.
        services.AddAgentLogSink(o => o.BufferLinesPerAgent = options.AgentLogBufferLines);

        // 5. Optional run store — Postgres-backed graph run history. Off-by-default; set
        //    VAIS_RUN_STORE_CONNECTION to an Npgsql connection string to enable. HTTP endpoints
        //    return 503 when not configured. Schema is created on first run automatically.
        if (!string.IsNullOrWhiteSpace(options.RunStoreConnection))
        {
            services.AddRunStore(o => o.ConnectionString = options.RunStoreConnection);
            services.AddSingleton<ISelfCheckProbe>(new PostgresSelfCheckProbe(
                "postgres-run-store", options.RunStoreConnection,
                "SELECT COUNT(*) FROM vais_graph_runs LIMIT 1"));
        }

        if (!string.IsNullOrWhiteSpace(options.AgentRunStoreConnection))
        {
            services.AddAgentRunStore(o => o.ConnectionString = options.AgentRunStoreConnection);
            services.AddSingleton<ISelfCheckProbe>(new PostgresSelfCheckProbe(
                "postgres-agent-run-store", options.AgentRunStoreConnection,
                "SELECT COUNT(*) FROM vais_agent_runs LIMIT 1"));
        }

        if (!string.IsNullOrWhiteSpace(options.GatewayEventStoreConnection))
        {
            services.AddGatewayEventStore(o =>
            {
                o.ConnectionString = options.GatewayEventStoreConnection;
                if (!string.IsNullOrWhiteSpace(options.GatewayId))
                    o.GatewayId = options.GatewayId;
            });
            services.AddSingleton<ISelfCheckProbe>(new PostgresSelfCheckProbe(
                "postgres-gateway-event-store", options.GatewayEventStoreConnection,
                "SELECT COUNT(*) FROM vais_gateway_events LIMIT 1"));
        }

        if (!string.IsNullOrWhiteSpace(options.McpEventStoreConnection))
        {
            services.AddMcpEventStore(o =>
            {
                o.ConnectionString = options.McpEventStoreConnection;
                if (!string.IsNullOrWhiteSpace(options.McpServerId))
                    o.ServerId = options.McpServerId;
            });
            services.AddSingleton<ISelfCheckProbe>(new PostgresSelfCheckProbe(
                "postgres-mcp-event-store", options.McpEventStoreConnection,
                "SELECT COUNT(*) FROM vais_mcp_events LIMIT 1"));
        }

        if (!string.IsNullOrWhiteSpace(options.McpGatewayEventStoreConnection))
        {
            services.AddMcpGatewayEventStore(o =>
            {
                o.ConnectionString = options.McpGatewayEventStoreConnection;
                if (!string.IsNullOrWhiteSpace(options.McpGatewayId))
                    o.GatewayId = options.McpGatewayId;
            });
            services.AddSingleton<ISelfCheckProbe>(new PostgresSelfCheckProbe(
                "postgres-mcp-gateway-event-store", options.McpGatewayEventStoreConnection,
                "SELECT COUNT(*) FROM vais_mcp_gateway_events LIMIT 1"));
        }

        // 6. Diagnostics filter-status tracker (always; lightweight singleton).
        services.AddSingleton<IFilterStatusTracker, FilterStatusTracker>();

        // 7. Optional observability. Off unless the OTel endpoint, console-exporter toggle,
        //    or the diagnostic span buffer is enabled; off-by-default keeps hello-world overhead zero.
        ConfigureObservability(services, options);

        // Self-check: infrastructure service probes registered after stores so all
        // AddXxx calls above have already registered their own initializer services.
        if (!string.IsNullOrWhiteSpace(options.PostgresConnection))
        {
            var isRequired = (options.Mode == "localhost" &&
                              (options.LocalhostPersistence == LocalhostPersistenceMode.Postgres
                               || options.LocalhostPubSubPersistence == LocalhostPersistenceMode.Postgres))
                          || (options.Mode == "clustered" && options.ClusteringBackend == "postgres");
            services.AddSingleton<ISelfCheckProbe>(new PostgresSelfCheckProbe(
                "postgres-orleans", options.PostgresConnection, "SELECT 1", isRequired));
        }
        if (!string.IsNullOrWhiteSpace(options.RedisConnection))
            services.AddSingleton<ISelfCheckProbe>(new RedisSelfCheckProbe(options.RedisConnection));
        if (!string.IsNullOrWhiteSpace(options.OtelEndpoint)
            && Uri.TryCreate(options.OtelEndpoint, UriKind.Absolute, out var otelUri))
        {
            services.AddSingleton<ISelfCheckProbe>(new HttpSelfCheckProbe(
                "otel", $"{otelUri.Scheme}://{otelUri.Host}:{otelUri.Port}/healthz"));
        }
        if (!string.IsNullOrWhiteSpace(options.LangfuseHost))
            services.AddSingleton<ISelfCheckProbe>(new HttpSelfCheckProbe(
                "langfuse", options.LangfuseHost.TrimEnd('/') + "/api/health"));

        // 7. Optional OPA policy engine. Off-by-default → NullAgentPolicyEngine.Instance
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

        // 8. Health checks. Liveness (/healthz) maps to the default "catch-all" set; readiness
        //    (/readyz) filters on the "ready" tag and gates on the co-hosted silo reaching
        //    SiloStatus.Active. Probe tuning lives in the Helm chart (60s failure threshold).
        services.AddSingleton<SelfCheckResultsStore>();
        services.AddHealthChecks()
            .AddCheck<OrleansActiveHealthCheck>("orleans", tags: ["ready"])
            .AddCheck<SelfCheckHealthCheck>("self-check", tags: ["ready"]);

        // 9. Startup hosted services — run once on StartAsync, after Orleans becomes active.
        //    PluginManifestConsistencyCheck walks registered manifests and verifies handlers.
        //    BootManifestApplyService applies manifest files from VAIS_BOOT_MANIFESTS_DIRECTORY.
        services.AddHostedService<PluginManifestConsistencyCheck>();

        if (!string.IsNullOrWhiteSpace(options.BootManifestsDirectory))
        {
            services.AddHostedService(sp => new BootManifestApplyService(
                options.BootManifestsDirectory,
                sp.GetRequiredService<IAgentLifecycleManager>(),
                sp.GetRequiredService<IAgentGraphLifecycleManager>(),
                sp.GetRequiredService<ILlmGatewayConfigLifecycleManager>(),
                sp.GetRequiredService<IMcpGatewayConfigLifecycleManager>(),
                sp.GetRequiredService<IMcpServerLifecycleManager>(),
                sp.GetService<ILogger<BootManifestApplyService>>() ?? NullLogger<BootManifestApplyService>.Instance));
        }

        // RuntimeSelfCheckService runs LAST — after all initializers have created their schemas.
        services.AddHostedService<RuntimeSelfCheckService>();

        // 10. CORS — localhost mode: allow all local origins so the Workbench dev server (Vite,
        //    any port) connects without configuration. Explicit VAIS_CORS_ORIGINS overrides.
        //    Set VAIS_CORS_ORIGINS=disabled to opt out even in localhost mode.
        var corsDisabled = string.Equals(options.CorsOrigins, "disabled", StringComparison.OrdinalIgnoreCase);
        if (!corsDisabled && (options.Mode == "localhost" || !string.IsNullOrWhiteSpace(options.CorsOrigins)))
        {
            services.AddCors(cors => cors.AddDefaultPolicy(policy =>
            {
                if (!string.IsNullOrWhiteSpace(options.CorsOrigins))
                {
                    var origins = options.CorsOrigins.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    policy.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod();
                }
                else
                {
                    policy.SetIsOriginAllowed(o => Uri.TryCreate(o, UriKind.Absolute, out var uri)
                            && (uri.Host == "localhost" || uri.Host == "127.0.0.1"))
                        .AllowAnyHeader().AllowAnyMethod();
                }
            }));
        }
    }

    private static void ConfigureObservability(IServiceCollection services, RuntimeOptions options)
    {
        var otelEnabled = !string.IsNullOrWhiteSpace(options.OtelEndpoint) || options.OtelConsole;

        // Register the diagnostic span buffer early so the endpoint can resolve it even when
        // full OTel exporters are not configured.
        DiagSpanBuffer? diagBuffer = null;
        if (options.DiagSpanBufferEnabled)
        {
            diagBuffer = new DiagSpanBuffer();
            services.AddSingleton(diagBuffer);
            services.AddSingleton<IDiagSpanBuffer>(diagBuffer);
        }

        if (otelEnabled || options.DiagSpanBufferEnabled)
        {
            services.AddOpenTelemetry()
                .WithTracing(t =>
                {
                    t.AddAgenticInstrumentation();
                    if (diagBuffer is not null)
                        t.AddProcessor(new SimpleActivityExportProcessor(diagBuffer));
                    if (!string.IsNullOrWhiteSpace(options.OtelEndpoint))
                    {
                        // Do NOT set o.Endpoint in code — the SDK will NOT append /v1/traces
                        // when the endpoint is set programmatically, but it WILL when it reads
                        // OTEL_EXPORTER_OTLP_ENDPOINT from the environment. Let the SDK own it.
                        t.AddOtlpExporter(o =>
                        {
                            if (!string.IsNullOrWhiteSpace(options.OtelHeaders))
                                o.Headers = options.OtelHeaders;
                        });
                    }
                    if (options.OtelConsole)
                    {
                        t.AddConsoleExporter();
                    }
                })
                .WithMetrics(m =>
                {
                    m.AddAgenticInstrumentation();
                    m.AddPrometheusExporter();
                    if (!string.IsNullOrWhiteSpace(options.OtelEndpoint))
                    {
                        m.AddOtlpExporter(o =>
                        {
                            if (!string.IsNullOrWhiteSpace(options.OtelHeaders))
                                o.Headers = options.OtelHeaders;
                        });
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

            if (string.IsNullOrWhiteSpace(options.OtelEndpoint) && !options.OtelConsole)
            {
                Console.Error.WriteLine(
                    "[vais] WARNING: VAIS_LANGFUSE_PROJECT is set but neither VAIS_OTEL_ENDPOINT " +
                    "nor VAIS_OTEL_CONSOLE is configured. Langfuse traces will NOT be emitted. " +
                    "Set VAIS_OTEL_ENDPOINT to your OTLP collector endpoint.");
            }
        }
    }
}
