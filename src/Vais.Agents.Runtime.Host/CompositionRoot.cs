// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Vais.Agents.Runtime.Host.Diagnostics;
using Orleans.Configuration;
using Vais.Agents.Control;
using Vais.Agents.Control.Http;
using Vais.Agents.Control.InProcess;
using Vais.Agents.Control.Manifests;
using Vais.Agents.Control.Mcp;
using Vais.Agents.Control.Policy.Opa;
using Vais.Agents.Core;
using Vais.Agents.Core.PowerFx;
using Vais.Agents.Eval;
using Vais.Agents.Hosting.Orleans;
using Vais.Agents.Protocols.A2A;
using Vais.Agents.Hosting.InMemory;
using Vais.Agents.Observability.AgentLogs;
using Vais.Agents.Observability.AgentRunStore;
using Vais.Agents.Observability.GatewayEventStore;
using Vais.Agents.Observability.InterceptorTeeStore;
using Vais.Agents.Observability.RecipeProposalStore;
using Vais.Agents.Observability.Langfuse;
using Vais.Agents.Observability.McpEventStore;
using Vais.Agents.Observability.McpGatewayEventStore;
using Vais.Agents.Observability.OpenTelemetry;
using Vais.Agents.Observability.RunStore;
using Vais.Agents.Observability.RunHealthStore;
using Vais.Agents.Persistence.Postgres;
using Vais.Agents.Persistence.Redis;
using Vais.Agents.Runtime.Instantiation;
using Vais.Agents.Runtime.Instantiation.Guardrails;
using Vais.Agents.Runtime.Instantiation.ModelProviders;
using Vais.Agents.Runtime.Plugins;
using Vais.Agents.Runtime.Plugins.Python;
using Vais.Agents.Runtime.Plugins.Container;
using Vais.Agents.ScriptRuntime;
using Vais.Agents.Gateways.Prometheus;
using Vais.Agents.Gateways.Fallback;
using Vais.Agents.Gateways.Governance;
using Vais.Agents.Gateways.SemanticCache;
using Vais.Agents.Gateways.StructuredOutput;
using Vais.Agents.Gateways.McpGovernance;
using Vais.Agents.Runtime.Extensions;
using Vais.Agents.Gateways.McpSecurity;
using Vais.Agents.Gateways.McpReliability;
using Vais.Agents.Gateways.McpCache;
using System.Text.Json;
using Vais.Agents.Gateways.McpTransformation;
using Vais.Agents.Orchestration.Graph.MicrosoftAgentFramework;
using Vais.Agents.Gateways.OpenAiCompat;
using Vais.Agents.Control.Mcp.Server;

namespace Vais.Agents.Runtime.Host;

/// <summary>
/// Composition-root helpers for the runtime host. Split from <c>Program.cs</c> so that
/// unit tests in <c>Vais.Agents.Runtime.Host.Tests</c> can drive the same wiring against
/// a vanilla <see cref="IServiceCollection"/> without booting Orleans end-to-end.
/// </summary>
/// <remarks>
/// <para>
/// The public shape is two methods: <c>ConfigureSilo(ISiloBuilder, RuntimeOptions)</c>
/// runs inside the <c>UseOrleans</c> callback and wires clustering + grain storage + streams;
/// <c>ConfigureServices(IServiceCollection, RuntimeOptions)</c> runs against the
/// top-level <c>IServiceCollection</c> and wires durability sidecars, the HTTP control plane,
/// optional observability, and optional OPA.
/// </para>
/// <para>
/// <b>Structure.</b> <see cref="ConfigureServices"/> is a thin orchestrator over a set of
/// cohesive per-concern <c>Configure*</c> helpers (runtime foundation, registries, plugins,
/// gateway catalog, manifest pipeline, lifecycle managers, control plane, observability stores,
/// host infrastructure). The helpers may run in any order — see the override-discipline note.
/// </para>
/// <para>
/// <b>Override discipline.</b> Two host registrations override an in-memory default that a
/// referenced library registers via <c>TryAddSingleton</c>: <c>IIdempotencyStore</c>
/// (vs <c>AddAgentControlPlaneIdempotency</c>) and <c>IPrincipalMapper</c>
/// (vs <c>AddAgentControlPlaneJwtAuth</c>). Both use <c>services.Replace</c> so the durable
/// implementation wins <em>regardless of registration order</em>; the composition-root unit
/// tests lock this in. No other registration in this method is order-dependent.
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
            // Orleans persistent streams need a grain storage named "PubSubStore" for the
            // PubSub rendezvous grain; the Redis stream provider does not register one.
            silo.AddMemoryGrainStorage("PubSubStore");
            silo.UseAgenticRedisStreaming(conn);
        }
        else // postgres
        {
            var conn = options.PostgresConnection!;
            silo.UseAgenticPostgresClustering(conn);
            silo.AddAgenticPostgresGrainStorage(conn);
            silo.AddMemoryGrainStorage("PubSubStore"); // required by both stream providers below

            if (options.EffectiveStreamingBackend == "redis")
            {
                // Postgres clustering paired with Redis streams gives cross-silo event fan-out
                // without moving clustering to Redis. Orleans 10.x has no Postgres stream
                // provider (Streaming.AdoNet is not shipped). Opt in: VAIS_STREAMING_BACKEND=redis.
                silo.UseAgenticRedisStreaming(options.RedisConnection!);
            }
            else
            {
                // Default: in-silo memory streams — OrleansAgentEventBus still works but events
                // fan out within a silo, not across. Documented in install-the-runtime-locally.md.
                silo.AddMemoryStreams(OrleansAgentEventBus.StreamNamespace);
            }
        }
    }

    /// <summary>
    /// Wire the top-level <see cref="IServiceCollection"/>. Caller is expected to have
    /// already set up Orleans via <c>builder.Host.UseOrleans(...)</c>; this method only
    /// registers services that the ASP.NET Core host owns. A thin orchestrator over the
    /// per-concern <c>Configure*</c> helpers below.
    /// </summary>
    public static void ConfigureServices(IServiceCollection services, RuntimeOptions options, IConfiguration? configuration = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        options.EnsureValid();

        ConfigureDurability(services, options);
        ConfigureRuntimeAndRegistries(services);
        ConfigurePlugins(services, options);
        ConfigureCodeMode(services, options);
        ConfigureGatewayCatalog(services);
        ConfigureManifestPipeline(services, options);
        ConfigureLifecycleManagers(services, options, configuration);
        ConfigureGovernance(services, options);
        ConfigureControlPlaneAndAuth(services, options);
        ConfigureObservabilityStores(services, options);
        ConfigureHostInfra(services, options);
    }

    /// <summary>
    /// Orleans durability sidecars. Task store + checkpointer have no competing default
    /// wired in this host, so they are the sole ITaskStore / IGraphCheckpointer registrations
    /// and registration order is irrelevant. (The idempotency store DOES have a competing
    /// in-memory default from AddAgentControlPlaneIdempotency; it is installed
    /// order-independently via services.Replace in <see cref="ConfigureControlPlaneAndAuth"/>.)
    /// </summary>
    private static void ConfigureDurability(IServiceCollection services, RuntimeOptions options)
    {
        services.AddOrleansA2ATaskStore();

        // Checkpointer backend selection. Default: Orleans-grain-backed (durable, no extra config).
        // VAIS_CHECKPOINTER_CONNECTION opts into a Postgres-direct checkpointer instead — a simpler,
        // queryable store (schema auto-created on first use). Exactly one IGraphCheckpointer is
        // registered either way, so this is order-independent (no services.Replace needed).
        if (!string.IsNullOrWhiteSpace(options.CheckpointerConnection))
        {
            var dataSource = Npgsql.NpgsqlDataSource.Create(options.CheckpointerConnection!);
            services.AddSingleton<IGraphCheckpointer>(new PostgresGraphCheckpointer(dataSource));
            services.AddSingleton<ISelfCheckProbe>(new PostgresSelfCheckProbe(
                "postgres-checkpointer", options.CheckpointerConnection, "SELECT 1"));
        }
        else
        {
            services.AddOrleansGraphCheckpointer();
        }
    }

    /// <summary>
    /// Orleans-backed agent runtime + event bus + background tracker, the in-process graph
    /// event bus, and the durable registries (agent / graph / gateway configs / mcp server /
    /// container plugin / eval) plus eval, extensions, and the secret resolver.
    /// </summary>
    private static void ConfigureRuntimeAndRegistries(IServiceCollection services)
    {
        // IHttpClientFactory — required by plugin handlers that take IHttpClientFactory in
        // their constructor. AddHttpClient is idempotent; calling it here makes it available
        // in the plugin DI container without the plugin author having to register it manually.
        services.AddHttpClient();

        // Orleans-backed agent runtime (client-side) + stream-backed event bus. These
        // bind IAgentRuntime + IAgentContextAccessor + IAgentEventBus against IGrainFactory,
        // which the co-hosted silo exposes into the same DI container.
        services.AddOrleansAgentRuntime();
        services.AddOrleansAgentEventBus();
        services.AddOrleansAgentGraphEventBus();
        // Grain-backed run registry — cross-silo graph cancel/status/conflict (P1). Registered
        // before the in-process TryAdd fallback below so it wins in the runtime.
        services.AddOrleansGraphRunCoordinator();
        // Grain-backed invoke-lease store — session-mode call-token liveness reachable from any silo
        // (P1). Registered before the in-memory fallback in AddContainerGatewayCallToken so it wins.
        services.AddOrleansInvokeLeaseStore();

        // Background agent-as-tool tracker — durable, grain-backed, cluster-wide.
        // Required by AgentManifestTranslator when any localAgents entry sets
        // mode: Background. Without this registration the translator throws at
        // first activation of a coordinator that declares a background sub-agent.
        services.TryAddSingleton<IBackgroundAgentTracker>(sp =>
            new OrleansBackgroundAgentTracker(sp.GetRequiredService<IGrainFactory>()));

        // IAgentGraphEventBus — Orleans-streams-backed in the runtime (registered above via
        // AddOrleansAgentGraphEventBus). This TryAdd is the in-process fallback for hosts that did
        // not register the Orleans bus. Shared by AgentGraphLifecycleManager (publishes) and
        // RunStoreSubscriber (persists) — both resolve the same singleton.
        services.TryAddSingleton<IAgentGraphEventBus, InMemoryAgentGraphEventBus>();

        // v0.17 Pillar B — Orleans-backed durable registries so vais apply persists across
        // silo restart. Eval + extensions + secret resolver round out the runtime services
        // the translator and lifecycle managers depend on.
        services.AddOrleansAgentRegistry();
        services.AddOrleansAgentGraphRegistry();
        services.AddOrleansLlmGatewayConfigRegistry();
        services.AddOrleansMcpGatewayConfigRegistry();
        services.AddOrleansMcpServerRegistry();
        services.AddOrleansContainerPluginRegistry();
        services.AddOrleansEvalSuiteRegistry();
        services.AddOrleansEvalRunLifecycleManager();
        services.AddVaisAgentsEval();
        services.TryAddSingleton<IEvalAssertionKindRegistry>(new PassthroughEvalAssertionKindRegistry());
        services.AddVaisExtensions();
        services.TryAddSingleton<ISecretResolver>(_ => CompositeSecretResolver.CreateDefault());
    }

    /// <summary>
    /// Opt-in plugin loaders — assembly (v0.18), Python (v0.23), and container plugins. Each is
    /// gated on its directory option. The translator resolves the resulting registries/providers
    /// at activation, so this may run before or after <see cref="ConfigureManifestPipeline"/>.
    /// </summary>
    private static void ConfigurePlugins(IServiceCollection services, RuntimeOptions options)
    {
        // v0.18 Pillar C — plugin loader. The translator takes IPluginHandlerRegistry as an
        // optional ctor dependency resolved at activation, so this may be wired before or after
        // AddAgentManifestInstantiator; when no registry is present the translator falls through
        // to the v0.17 declarative path. Empty / whitespace PluginsDirectory → skip wiring
        // entirely (disabled mode). Non-existent directory is a loader no-op with an empty registry.
        if (!string.IsNullOrWhiteSpace(options.PluginsDirectory))
        {
            services.AddAgentPlugins(
                options.PluginsDirectory,
                new PluginLoaderOptions
                {
                    ReloadPolicy = options.PluginsHotReload,
                    DiagnoseUnloadLeaks = options.PluginsDiagnoseUnloadLeaks,
                });
        }

        // v0.23 Python-plugins pillar — opt-in via VAIS_PYTHON_PLUGINS_DIRECTORY. Registers an
        // INamedToolSourceProvider the translator resolves at activation (order-independent).
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
        // Container-MCP servers (transport: containerStdio) share the same gate because
        // they reuse the same Docker socket + ContainerPluginLoaderOptions (network, resource bounds).
        if (!string.IsNullOrWhiteSpace(options.ContainerPluginsDirectory))
        {
            services.AddContainerPlugins(o =>
            {
                o.PluginsDirectory = options.ContainerPluginsDirectory;
                o.PluginNetwork    = options.DockerPluginNetwork;
                o.RenewTokenTtlSeconds = options.ContainerPluginRenewTokenTtlSeconds;
            });
            services.AddContainerMcpServers();
        }
    }

    // Code-mode (ScriptRuntime primitive) — opt-in via VAIS_CODE_MODE_ENABLED. Registers the
    // run_code tool factory the manifest translator resolves, the typed sidecar client, and the
    // call-token service the container-gateway endpoints validate (the script's tool calls route
    // back through /v1/container-gateway, mapped in Program when code-mode is on).
    private static void ConfigureCodeMode(IServiceCollection services, RuntimeOptions options)
    {
        if (!options.CodeModeEnabled)
        {
            return;
        }

        services.AddContainerGatewayCallToken();
        services.AddScriptRuntime(o =>
        {
            o.SidecarBaseUrl = options.ScriptRuntimeUrl;
            // The URL the sidecar uses to call tools back into this runtime — same base the
            // container plugins use, set per topology via VAIS_INTERNAL_GATEWAY_URL.
            o.GatewayBaseUrl = options.InternalGatewayBaseUrl;
        });
    }

    /// <summary>
    /// GCF-20/21 — the named LLM + tool gateway middleware catalog plus the composite factories
    /// that resolve them. The factories collect the named registrations via IEnumerable&lt;&gt;
    /// injection, so order relative to the named registrations is irrelevant.
    /// </summary>
    private static void ConfigureGatewayCatalog(IServiceCollection services)
    {
        // Each AddNamed* call registers one NamedL*GatewayMiddlewareRegistration singleton;
        // the DefaultL*GatewayMiddlewareFactory collects them all via IEnumerable<> injection.
        // Core LLM middleware
        services.AddNamedLlmGatewayMiddleware_LlmLogging();
        services.AddNamedLlmGatewayMiddleware_LlmUsage();
        services.AddNamedLlmGatewayMiddleware_LlmOtel();
        services.AddNamedLlmGatewayMiddleware_LlmPromptEnrichment();
        // Package LLM middleware
        services.AddNamedLlmGatewayMiddleware_Prometheus();
        services.AddNamedLlmGatewayMiddleware_LlmRateLimit();
        RegisterManifestDrivenFallback(services);
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
        // Composite factories — resolve the named registrations via IEnumerable<> injection,
        // which collects them regardless of registration order relative to these factories.
        services.AddDefaultLlmGatewayMiddlewareFactory();
        services.AddDefaultToolGatewayMiddlewareFactory();
    }

    /// <summary>
    /// The manifest-instantiation pipeline: translator + provider pool, the plugin-reload and
    /// MCP-connection invalidation hooks, physical MCP servers, the built-in model providers and
    /// guardrails, and the <c>ConfigureAgentGrains</c> factory that drives translation at grain
    /// activation.
    /// </summary>
    private static void ConfigureManifestPipeline(IServiceCollection services, RuntimeOptions options)
    {
        services.AddAgentManifestInstantiator();
        services.AddGrainReactivationPluginReloadHook();
        services.AddPhysicalMcpServers();
        services.AddBuiltinModelProviders();
        services.AddBuiltinGuardrails();
        ConfigureDomainOntologyCartridge(services, options);

        services.ConfigureAgentGrains(async (sp, id, ct) =>
            await sp.GetRequiredService<IAgentManifestTranslator>().TranslateForGrain(sp, id, ct).ConfigureAwait(false));
    }

    /// <summary>
    /// Plan C1 south cartridge wiring. Registers a shared
    /// <see cref="InMemoryDomainOntologyArtifactRegistry"/> + a singleton
    /// <see cref="CachedDomainOntologyToolListShaper"/>. When
    /// <c>VAIS_DOMAIN_ONTOLOGY_DIR</c> is set, every <c>*.domain-ontology.json</c> file in
    /// that directory is registered at startup so virtual MCP servers with
    /// <c>OntologyRef</c> resolve immediately.
    /// </summary>
    private static void ConfigureDomainOntologyCartridge(IServiceCollection services, RuntimeOptions options)
    {
        services.TryAddSingleton<IDomainOntologyArtifactRegistry>(sp =>
        {
            var registry = new InMemoryDomainOntologyArtifactRegistry();
            var dir = Environment.GetEnvironmentVariable("VAIS_DOMAIN_ONTOLOGY_DIR");
            if (!string.IsNullOrWhiteSpace(dir))
                registry.RegisterAll(DomainOntologyArtifactLoader.LoadAllFromDirectory(dir));
            return registry;
        });
        services.TryAddSingleton<CachedDomainOntologyToolListShaper>();

        // Plan C2 capability fabric defaults — the components are wired into the translator
        // automatically (sub-agent description overlay, C2-3) and made available for
        // deployer-supplied extensions to consume (capability-map middleware, delegation
        // governance, AllowedTools resolver). Deployers swap in custom builders / policies
        // by registering their own before this method runs (services.TryAddSingleton).
        services.TryAddSingleton<IAgentCapabilityMapBuilder, AgentCapabilityMapBuilder>();
        services.TryAddSingleton<IOntologyAllowedToolsResolver, OntologyAllowedToolsResolver>();
        services.TryAddSingleton<IDelegationPolicy>(_ => AllowAllDelegationPolicy.Instance);

        // Plan D — trajectory tee. The in-memory store ships as the always-on default so the
        // tee has somewhere to land; deployers swap in a persistent store (Postgres,
        // Langfuse, ClickHouse, ...) by registering an IInterceptorTeeStore before this runs.
        // RecordingInterceptorTee replaces C1's NullInterceptorTee default whenever a store
        // is available; observability-kind interceptors that call EmitAsync land a structured
        // event in the store with zero deployer wiring.
        services.TryAddSingleton<IInterceptorTeeStore, InMemoryInterceptorTeeStore>();
        services.TryAddSingleton<TrajectoryArgumentRedactor>(_ => TrajectoryArgumentRedactor.Default);
        services.TryAddSingleton<IInterceptorTee, RecordingInterceptorTee>();

        // Plan D — induced recipes. The in-memory proposal store ships as default; the
        // high-risk gate is wired here so that approving a High-risk proposal flows through
        // the existing IApprovalStore (Plan B): first DecideAsync call creates a pending
        // ApprovalRequest and throws ApprovalRequiredException (mapped to 202 at the REST
        // boundary); operator approves it via 'vais approvals approve <id>'; re-running
        // DecideAsync finds the matching approval and flips the proposal to Approved.
        var overlayPathForRecipes = options.OntologyOverlayPath; // captured for decorator wiring
        services.TryAddSingleton<IRecipeProposalStore>(sp =>
        {
            var inner = (IRecipeProposalStore)new InMemoryRecipeProposalStore(BuildRecipeApprovalGate(sp));
            return MaybeWrapWithOverlayPublishing(sp, inner, overlayPathForRecipes);
        });

        // Plan D D-8/D-9: register the behavioral inducer as the always-on default so
        // `vais recipes propose` works out of the box. Deployers can override by registering
        // a different IRecipeInducer (e.g. LlmAssistedRecipeInducer decorator) before this.
        services.TryAddSingleton<IRecipeInducer>(sp =>
            new BehavioralRecipeInducer(sp.GetRequiredService<IInterceptorTeeStore>()));

        // Plan D D-13: register the JSON overlay writer whenever an overlay path is set so the
        // approval-side-effect decorator can pick it up. The writer itself is stateless.
        if (!string.IsNullOrWhiteSpace(options.OntologyOverlayPath))
        {
            services.TryAddSingleton<IOntologyOverlayWriter, JsonOntologyOverlayWriter>();
        }
    }

    /// <summary>
    /// Plan D D-14 — when both an <see cref="IOntologyOverlayWriter"/> and an overlay path
    /// are configured, wrap the proposal store so approved recipes land in the on-disk
    /// overlay and trigger a catalog reload. Returns <paramref name="inner"/> unchanged when
    /// any dependency is missing (overlay path absent ⇒ no place to write).
    /// </summary>
    private static IRecipeProposalStore MaybeWrapWithOverlayPublishing(IServiceProvider sp, IRecipeProposalStore inner, string? overlayPath)
    {
        if (string.IsNullOrWhiteSpace(overlayPath)) return inner;
        var writer = sp.GetService<IOntologyOverlayWriter>();
        if (writer is null) return inner;
        var reloader = sp.GetService<IOntologyCatalogReloader>();
        var logger = sp.GetService<ILogger<OverlayPublishingRecipeProposalStoreDecorator>>();
        return new OverlayPublishingRecipeProposalStoreDecorator(inner, writer, overlayPath, reloader, logger);
    }

    private static Func<RecipeProposal, string, CancellationToken, ValueTask>? BuildRecipeApprovalGate(IServiceProvider sp)
    {
        var approvalStore = sp.GetService<Vais.Agents.Control.IApprovalStore>();
        if (approvalStore is null) return null; // no approval subsystem wired → no gate
        return async (proposal, decidedBy, ct) =>
        {
            var hash = RecipeProposalHash(proposal);
            var approved = await approvalStore.FindApprovedAsync("Recipe", proposal.ProposalId, hash, ct).ConfigureAwait(false);
            if (approved is not null) return;
            var pending = await approvalStore.CreatePendingAsync("Recipe", proposal.ProposalId, hash, decidedBy, ct).ConfigureAwait(false);
            throw new Vais.Agents.Control.ApprovalRequiredException("Recipe", proposal.ProposalId, pending.RequestId);
        };
    }

    private static string RecipeProposalHash(RecipeProposal p)
    {
        // Stable across re-runs of the same proposal; changing body / risk / support invalidates
        // the approval. Status / reviewer fields are deliberately excluded — those move on
        // decision.
        var canonical = $"{p.Kind}|{p.Concept}|{p.Body}|{p.RiskLevel}|{p.Support}|{p.Confidence:R}";
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexStringLower(bytes);
    }

    /// <summary>
    /// Lifecycle managers (agent, graph, gateway configs, MCP server) + the audit log and the
    /// cross-runtime invocation dependencies they need: HTTP context accessor, remote invoker
    /// (v0.20, configured from <c>Vais:RemoteRuntimes</c>), A2A invoker, and the PowerFx evaluator.
    /// </summary>
    private static void ConfigureLifecycleManagers(IServiceCollection services, RuntimeOptions options, IConfiguration? configuration)
    {
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
        if (options.A2aEnabled)
            services.AddA2AGraphNodeInvoker();
        if (options.PowerFxEnabled)
            services.AddPowerFxExpressionEvaluator();
        // Graph run registry — in-process by default; a clustered deployment can register an
        // Orleans-backed IGraphRunCoordinator earlier so cancel/status work cluster-wide (P1).
        services.TryAddSingleton<IGraphRunCoordinator, InProcessGraphRunCoordinator>();
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
                        expressionEvaluator: sp.GetService<IGraphExpressionEvaluator>(),
                        errorInterceptorComposer: sp.GetService<Vais.Agents.Runtime.Extensions.IExtensionChainComposer>(),
                        graphNodeComposer: sp.GetService<Vais.Agents.Runtime.Extensions.IExtensionChainComposer>(),
                        logger: sp.GetService<ILogger<MafGraphOrchestrator<IDictionary<string, JsonElement>>>>()),
                coordinator: sp.GetService<IGraphRunCoordinator>());
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
    }

    /// <summary>
    /// Plan B control-plane governance — all opt-in via runtime config; defaults preserve the
    /// allow-all, no-audit, no-approval behaviour. Runs after the lifecycle managers (which add the
    /// default <c>LoggerAuditLog</c>) and before <see cref="ConfigureControlPlaneAndAuth"/> /
    /// <c>AddMcpDesignServer</c> (which <c>TryAdd</c>s the base-only ontology catalog), so the
    /// overlay-backed catalog and RBAC engine win.
    /// </summary>
    private static void ConfigureGovernance(IServiceCollection services, RuntimeOptions options)
    {
        // Ontology overlay → merged catalog (RBAC roles + risk tags + describe overrides).
        // Plan D D-14: when an overlay path is configured, the catalog is wrapped in
        // HotReloadableOntologyCatalog so an approved RecipeProposal landing in the file
        // (via OverlayPublishingRecipeProposalStoreDecorator) is visible to vais.describe on
        // the next read — no runtime restart.
        if (!string.IsNullOrWhiteSpace(options.OntologyOverlayPath))
        {
            var overlay = OntologyOverlayLoader.LoadFromFile(options.OntologyOverlayPath);
            var initialCatalog = OntologyCatalog.BuildFromEmbeddedBase(overlay);
            var hotReloadable = new HotReloadableOntologyCatalog(initialCatalog, options.OntologyOverlayPath);
            services.AddSingleton<IOntologyCatalog>(hotReloadable);
            services.AddSingleton<IOntologyCatalogReloader>(hotReloadable);

            // RBAC: overlay author-roles authorize mutating verbs per JWT scope (replaces allow-all).
            if (overlay.AuthorRoles is { IsEmpty: false } roles)
            {
                services.AddAuthorRolesPolicy(roles);
            }
        }

        // JSONL audit trail (replaces the default LoggerAuditLog).
        if (!string.IsNullOrWhiteSpace(options.AuditLogPath))
        {
            services.Replace(ServiceDescriptor.Singleton<IAuditLog>(new JsonlAuditLog(options.AuditLogPath!)));
        }

        // Approval queue for high-risk mutations — Orleans grain-backed (durable, cluster-wide, P1).
        if (options.ApprovalsEnabled)
        {
            services.AddSingleton<IApprovalStore>(sp => new OrleansApprovalStore(sp.GetRequiredService<IGrainFactory>()));
            services.AddApprovalGate();
        }
    }

    /// <summary>
    /// HTTP control plane (routes + idempotency middleware + OpenAPI), optional JWT auth +
    /// principal mapping, and the OpenAI-compatible gateway. The durable idempotency store and
    /// the ServiceAccount principal mapper are installed via <c>services.Replace</c> so they win
    /// over the in-memory library defaults regardless of order.
    /// </summary>
    private static void ConfigureControlPlaneAndAuth(IServiceCollection services, RuntimeOptions options)
    {
        services.AddAgentControlPlane();
        if (options.IdempotencyEnabled)
        {
            // AddAgentControlPlaneIdempotency TryAdds an in-memory IIdempotencyStore default.
            // Replace swaps in the durable Orleans store regardless of registration order —
            // no "register Orleans first" footgun.
            services.AddAgentControlPlaneIdempotency();
            services.Replace(ServiceDescriptor.Singleton<IIdempotencyStore>(
                sp => new OrleansIdempotencyStore(
                    sp.GetRequiredService<IGrainFactory>(),
                    OrleansIdempotencyStore.DefaultTtl)));
        }
        services.AddAgentControlPlaneOpenApi();

        // v0.30 JWT authentication + principal mapping. Off-by-default — existing localhost
        // semantics unchanged. When VAIS_JWT_AUTHORITY is set the full bearer-token pipeline
        // is wired. AddAgentControlPlaneJwtAuth TryAdds DefaultPrincipalMapper; when the SA
        // mapper is opted in, services.Replace swaps it in order-independently.
        if (!string.IsNullOrWhiteSpace(options.JwtAuthority))
        {
            services.AddAgentControlPlaneJwtAuth(o =>
            {
                o.Authority = options.JwtAuthority;
                o.RequireHttpsMetadata = options.JwtRequireHttpsMetadata;
                if (!string.IsNullOrWhiteSpace(options.JwtAudience))
                {
                    o.Audience = options.JwtAudience;
                }
                else
                {
                    // No audience configured ⇒ don't validate it (matches RuntimeOptions.JwtAudience docs).
                    // Without this, JwtBearer's default ValidateAudience=true rejects every token for lack
                    // of a configured audience.
                    o.TokenValidationParameters.ValidateAudience = false;
                }
            });

            if (options.UseSaPrincipalMapper)
            {
                services.Replace(ServiceDescriptor.Singleton<IPrincipalMapper, ServiceAccountPrincipalMapper>());
            }

            // Co-hosting fix: AddOrleansAgentRuntime registered OrleansAgentContextAccessor as the
            // IAgentContextAccessor (silo-side, Orleans RequestContext) and won the slot via first
            // TryAdd, so AddAgentControlPlaneJwtAuth's AsyncLocalAgentContextAccessor mapping became a
            // no-op. Control-plane ingress (lifecycle managers, endpoint gate, approval + MCP mutation
            // handlers) reads the principal the HTTP/MCP middleware pushed onto AsyncLocalAgentContextAccessor;
            // resolving the Orleans accessor on the ingress thread yields an empty RequestContext, so
            // authenticated applies would synthesize an anonymous principal and RBAC would deny them.
            // The composite prefers the ingress principal when present and falls back to the Orleans
            // accessor on silo grain turns (where the AsyncLocal slot is empty).
            services.Replace(ServiceDescriptor.Singleton<IAgentContextAccessor>(sp =>
                new IngressFirstAgentContextAccessor(
                    sp.GetRequiredService<AsyncLocalAgentContextAccessor>(),
                    new OrleansAgentContextAccessor())));
        }

        // OpenAI-compatible gateway — exposes GET /v1/models and POST /v1/chat/completions.
        // PassThroughIdentityResolver accepts any bearer token (single-tenant / dev mode).
        // InMemoryModelRouter with no aliases means non-agent/graph model IDs return 404;
        // agent: and graph: prefixes are handled before the router is consulted.
        services.AddPassThroughIdentityResolver();
        services.AddInMemoryModelRouter(_ => { });
        services.AddOpenAiCompatGateway();

        // Read-only design-tools MCP server (Plan A). Mounted at /design-mcp (see Program.cs).
        services.AddMcpDesignServer();
    }

    /// <summary>
    /// The always-on in-memory agent log sink plus the optional Postgres-backed run / agent-run /
    /// gateway-event / mcp-event / mcp-gateway-event stores. Each store is off-by-default (gated on
    /// its connection string) and registers a matching <see cref="ISelfCheckProbe"/> when enabled.
    /// </summary>
    private static void ConfigureObservabilityStores(IServiceCollection services, RuntimeOptions options)
    {
        // Agent log sink — in-memory ring buffer for agent grain and Python subprocess stdout.
        // Always registered; no connection string required. Buffer cap configurable via
        // VAIS_AGENT_LOG_BUFFER_LINES (default 500). HTTP endpoint returns entries from sink.
        services.AddAgentLogSink(o => o.BufferLinesPerAgent = options.AgentLogBufferLines);

        // Optional run store — Postgres-backed graph run history. Off-by-default; set
        // VAIS_RUN_STORE_CONNECTION to an Npgsql connection string to enable. HTTP endpoints
        // return 503 when not configured. Schema is created on first run automatically.
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

        // Part 2a — shared failure taxonomy catalog. When VAIS_FAILURE_ONTOLOGY_OVERLAY_PATH is set,
        // loads a *.failure-ontology.json overlay from that directory and builds an OverlaidFailureOntologyCatalog.
        // Otherwise falls back to the auto-derived base (no I/O). The catalog is registered unconditionally
        // so RunHealthSignalSubscriber and the eval assertions can always resolve it.
        var overlayPath = options.FailureOntologyOverlayPath;
        if (!string.IsNullOrWhiteSpace(overlayPath))
        {
            var overlay = FailureOntologyOverlayLoader.LoadAllFromDirectory(overlayPath);
            services.AddSingleton<IFailureOntologyCatalog>(new OverlaidFailureOntologyCatalog(overlay));
        }
        else
        {
            services.AddSingleton<IFailureOntologyCatalog>(AutoDerivedFailureOntologyCatalog.Instance);
        }

        // Part 2b — failure attribution registry + index. The registry is populated at startup from
        // *.failure-attribution.json files in VAIS_FAILURE_ATTRIBUTION_DIR; the index is an in-memory
        // map populated at agent activation. Both registered unconditionally.
        var failureAttributionRegistry = new InMemoryFailureAttributionRegistry();
        var failureAttributionDir = options.FailureAttributionDir;
        if (!string.IsNullOrWhiteSpace(failureAttributionDir))
        {
            var artifacts = FailureAttributionArtifactLoader.LoadAllFromDirectory(failureAttributionDir);
            failureAttributionRegistry.RegisterAll(artifacts);
        }
        services.AddSingleton<IFailureAttributionRegistry>(failureAttributionRegistry);
        services.AddSingleton<IFailureAttributionIndex>(new InMemoryFailureAttributionIndex());

        // Run-health signal store. When VAIS_RUN_HEALTH_STORE_CONNECTION is set, the
        // RunHealthSignalSubscriber persists the mechanical-failure events from the agent event
        // bus (recovered tool errors, LLM retries/fallbacks, degraded turns, guardrail trips,
        // turn failures) so a run that "completed" still reveals whether it degraded.
        if (!string.IsNullOrWhiteSpace(options.RunHealthStoreConnection))
        {
            services.AddRunHealthStore(o => o.ConnectionString = options.RunHealthStoreConnection);
            services.AddSingleton<ISelfCheckProbe>(new PostgresSelfCheckProbe(
                "postgres-run-health-store", options.RunHealthStoreConnection,
                "SELECT COUNT(*) FROM vais_run_health_signals LIMIT 1"));
            // The aggregator folds the run-health signals + gateway/MCP by-run + graph nodes +
            // background sub-runs into a per-run RunHealth for GET /graphs/{id}/runs/{runId} and
            // `vais diagnose`. Registered only when the run-health store is configured.
            services.AddSingleton<IRunHealthAggregator, RunHealthAggregator>();
        }

        // Plan D — Postgres-backed trajectory store. When VAIS_INTERCEPTOR_TEE_STORE_CONNECTION
        // is set, this registration takes precedence over the in-memory ring default wired
        // earlier in ConfigureDomainOntologyCartridge. Schema auto-creates on first start;
        // RecordingInterceptorTee (already registered) writes into whichever store wins.
        if (!string.IsNullOrWhiteSpace(options.InterceptorTeeStoreConnection))
        {
            services.AddPostgresInterceptorTeeStore(o => o.ConnectionString = options.InterceptorTeeStoreConnection);
            services.AddSingleton<ISelfCheckProbe>(new PostgresSelfCheckProbe(
                "postgres-interceptor-tee-store", options.InterceptorTeeStoreConnection,
                "SELECT COUNT(*) FROM vais_trajectory_events LIMIT 1"));
        }

        // Plan D — Postgres-backed recipe proposal store. When VAIS_RECIPE_PROPOSAL_STORE_CONNECTION
        // is set, this replaces the in-memory default. The high-risk gate (resolved at registration
        // time) is hoisted into the Postgres registration so it survives the AddSingleton swap.
        if (!string.IsNullOrWhiteSpace(options.RecipeProposalStoreConnection))
        {
            services.AddPostgresRecipeProposalStore(
                o => o.ConnectionString = options.RecipeProposalStoreConnection,
                highRiskApprovalCheckFactory: BuildRecipeApprovalGate);
            services.AddSingleton<ISelfCheckProbe>(new PostgresSelfCheckProbe(
                "postgres-recipe-proposal-store", options.RecipeProposalStoreConnection,
                "SELECT COUNT(*) FROM vais_recipe_proposals LIMIT 1"));
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
    }

    /// <summary>
    /// Host-level infrastructure: the diagnostics filter-status tracker, optional observability
    /// (OTel + Langfuse), infrastructure self-check probes, optional OPA policy engine, health
    /// checks, startup hosted services, and CORS.
    /// </summary>
    private static void ConfigureHostInfra(IServiceCollection services, RuntimeOptions options)
    {
        // Diagnostics filter-status tracker (always; lightweight singleton).
        services.AddSingleton<IFilterStatusTracker, FilterStatusTracker>();

        // Optional observability. Off unless the OTel endpoint, console-exporter toggle,
        // or the diagnostic span buffer is enabled; off-by-default keeps hello-world overhead zero.
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

        // Optional OPA policy engine. Off-by-default → NullAgentPolicyEngine.Instance
        // (AllowAll) wins. AddOpaPolicyEngine uses TryAddSingleton<IAgentPolicyEngine>,
        // so it only binds when no prior registration exists — the startup log records
        // which engine is active so the default-open behaviour is never silent.
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

        // Health checks. Liveness (/healthz) maps to the default "catch-all" set; readiness
        // (/readyz) filters on the "ready" tag and gates on the co-hosted silo reaching
        // SiloStatus.Active. Probe tuning lives in the Helm chart (60s failure threshold).
        services.AddSingleton<SelfCheckResultsStore>();
        services.AddHealthChecks()
            .AddCheck<OrleansActiveHealthCheck>("orleans", tags: ["ready"])
            .AddCheck<SelfCheckHealthCheck>("self-check", tags: ["ready"]);

        // Startup hosted services — run once on StartAsync, after Orleans becomes active.
        // PluginManifestConsistencyCheck walks registered manifests and verifies handlers.
        // BootManifestApplyService applies manifest files from VAIS_BOOT_MANIFESTS_DIRECTORY.
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
                sp.GetRequiredService<IEvalSuiteRegistry>(),
                sp.GetService<ILogger<BootManifestApplyService>>() ?? NullLogger<BootManifestApplyService>.Instance));
        }

        // RuntimeSelfCheckService runs LAST — after all initializers have created their schemas.
        services.AddHostedService<RuntimeSelfCheckService>();

        // CORS — localhost mode: allow all local origins so the Workbench dev server (Vite,
        // any port) connects without configuration. Explicit VAIS_CORS_ORIGINS overrides.
        // Set VAIS_CORS_ORIGINS=disabled to opt out even in localhost mode.
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

            // Per-section OTel tags (vais.request.section.*) on the agent-turn span. Same gate as
            // the OTel pipeline above, so it stays zero-cost when tracing is off.
            services.AddAgenticOpenTelemetrySectionSink();
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

            // Per-section Langfuse tags (langfuse.section.*) + section_breakdown metadata on the
            // exported span, so sectioned prompt composition shows up in the Langfuse trace UI.
            services.AddLangfuseSectionEnrichment();

            if (string.IsNullOrWhiteSpace(options.OtelEndpoint) && !options.OtelConsole)
            {
                Console.Error.WriteLine(
                    "[vais] WARNING: VAIS_LANGFUSE_PROJECT is set but neither VAIS_OTEL_ENDPOINT " +
                    "nor VAIS_OTEL_CONSOLE is configured. Langfuse traces will NOT be emitted. " +
                    "Set VAIS_OTEL_ENDPOINT to your OTLP collector endpoint.");
            }
        }
    }

    // Registers "Fallback" as a manifest-driven named LLM gateway middleware.
    // Each pool entry in spec.Params is a full model spec — same shape as a top-level
    // spec.model block (provider, id, apiKeyRef, baseUrlRef, temperature, topP,
    // maxTokens, responseFormat). ICompletionProviderPool resolves and caches the
    // provider instances. Called from ConfigureGatewayCatalog instead of
    // AddNamedLlmGatewayMiddleware_Fallback() so the factory can resolve
    // ICompletionProviderPool without the Fallback package needing a dependency on
    // Runtime.Instantiation.
    private static void RegisterManifestDrivenFallback(IServiceCollection services)
    {
        services.AddSingleton(sp =>
        {
            var providerPool = sp.GetRequiredService<ICompletionProviderPool>();
            return new NamedLlmGatewayMiddlewareRegistration(
                "Fallback",
                (spec, _) =>
                {
                    var providers = BuildProviderPool(spec.Params, providerPool);
                    return new LlmFallbackMiddleware(new InMemoryFallbackProviderPool(providers));
                });
        });
    }

    private static ICompletionProvider[] BuildProviderPool(JsonElement? paramsEl, ICompletionProviderPool pool)
    {
        var providers = new List<ICompletionProvider>();
        foreach (var modelSpec in FallbackPoolManifestParser.ParsePool(paramsEl))
        {
            // GetAsync is async; safe to block here — runs once at agent activation, not per request.
            providers.Add(pool.GetAsync(modelSpec).AsTask().GetAwaiter().GetResult());
        }
        return [.. providers];
    }

    private sealed class PassthroughEvalAssertionKindRegistry : IEvalAssertionKindRegistry
    {
        public bool IsRegistered(string kind) => true;
    }
}
