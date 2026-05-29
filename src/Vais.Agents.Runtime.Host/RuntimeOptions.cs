// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Vais.Agents.Control.Policy.Opa;
using Vais.Agents.Runtime.Plugins;

namespace Vais.Agents.Runtime.Host;

/// <summary>
/// Grain storage backend for <c>localhost</c> mode.
/// </summary>
internal enum LocalhostPersistenceMode
{
    /// <summary>In-process memory storage — default, no external deps.</summary>
    Memory,
    /// <summary>Postgres via ADO.NET / Npgsql — requires <c>VAIS_POSTGRES_CONNECTION</c>.</summary>
    Postgres,
}

/// <summary>
/// Runtime-host composition inputs — derived from env vars at startup, or
/// constructed directly by unit tests. Keeping this a record lets tests
/// exercise <see cref="CompositionRoot"/> without touching process env state.
/// </summary>
internal sealed record RuntimeOptions
{
    public const string DefaultMode = "localhost";
    public const string DefaultClusteringBackend = "redis";

    /// <summary>
    /// Default directory scanned for v0.18 plugin assemblies. Matches the path baked into the
    /// runtime container image (v0.16 Pillar A decision #9 / FHS). Hosts that want to disable
    /// the plugin loader set <c>VAIS_PLUGINS_DIRECTORY</c> to an empty string.
    /// </summary>
    public const string DefaultPluginsDirectory = "/var/lib/vais/plugins";

    /// <summary><c>localhost</c> (memory streams + storage) or <c>clustered</c>.</summary>
    public string Mode { get; init; } = DefaultMode;

    /// <summary><c>redis</c> (default) or <c>postgres</c>. Ignored in localhost mode.</summary>
    public string ClusteringBackend { get; init; } = DefaultClusteringBackend;

    public string? RedisConnection { get; init; }
    public string? PostgresConnection { get; init; }

    /// <summary>
    /// Orleans stream provider for the event buses (<see cref="Vais.Agents.IAgentEventBus"/> +
    /// <c>IAgentGraphEventBus</c>): <c>memory</c> (same-silo only) or <c>redis</c> (cross-silo).
    /// Null/unset resolves via <see cref="EffectiveStreamingBackend"/> to <c>redis</c> when
    /// <see cref="ClusteringBackend"/> is redis, otherwise <c>memory</c>. Set
    /// <c>VAIS_STREAMING_BACKEND=redis</c> (plus <see cref="RedisConnection"/>) on a
    /// Postgres-clustered deployment to get cross-silo event fan-out without moving clustering to
    /// Redis — Orleans 10.x ships no Postgres stream provider. Ignored in localhost mode.
    /// </summary>
    public string? StreamingBackend { get; init; }

    /// <summary>Resolved stream provider after applying defaults: <c>memory</c> or <c>redis</c>.</summary>
    public string EffectiveStreamingBackend =>
        !string.IsNullOrWhiteSpace(StreamingBackend)
            ? StreamingBackend.ToLowerInvariant()
            : (Mode == "clustered" && ClusteringBackend == "redis" ? "redis" : "memory");

    public string? OtelEndpoint { get; init; }
    public string? OtelHeaders { get; init; }
    public bool OtelConsole { get; init; }

    public string? LangfuseProject { get; init; }

    public string? OpaBaseUrl { get; init; }
    public OpaFailMode OpaFailMode { get; init; } = OpaFailMode.Closed;
    public string? OpaDataPath { get; init; }

    /// <summary>
    /// v0.18 Pillar C plugin loader input. Null OR empty OR non-existent directory ⇒ loader
    /// skipped (no-op, empty registry, startup log records <c>plugins=disabled</c>). Defaults
    /// to <see cref="DefaultPluginsDirectory"/> so the container image's bind-mount path works
    /// out of the box; hosts without plugins set <c>VAIS_PLUGINS_DIRECTORY=""</c> to disable.
    /// </summary>
    public string? PluginsDirectory { get; init; } = DefaultPluginsDirectory;

    /// <summary>
    /// v0.22 Pillar F hot-reload policy. <see cref="ReloadPolicy.DrainAndSwap"/> registers
    /// <see cref="IPluginReloader"/> and starts the background filesystem watcher that
    /// swaps the handler registry on DLL changes without restarting the host. Defaults to
    /// <see cref="ReloadPolicy.Disabled"/> (v0.18-compatible, no watcher overhead). Set
    /// <c>VAIS_PLUGINS_RELOAD_POLICY=DrainAndSwap</c> in the container environment to enable.
    /// </summary>
    public ReloadPolicy PluginsHotReload { get; init; } = ReloadPolicy.Disabled;

    /// <summary>
    /// When <see langword="true"/> (the default), the reloader monitors the old
    /// <c>AssemblyLoadContext</c> after each unload and emits an operator-visible WARN if the
    /// context is still alive after 30 seconds. Set <c>VAIS_PLUGINS_DIAGNOSE_UNLOAD_LEAKS=false</c>
    /// to disable (useful when ALC churn is high and the diagnostic noise outweighs the benefit).
    /// </summary>
    public bool PluginsDiagnoseUnloadLeaks { get; init; } = true;

    /// <summary>
    /// v0.23 Python-plugins pillar. Directory scanned for Python plugin subfolders (each containing
    /// <c>plugin.yaml</c> + <c>pyproject.toml</c>). Null or empty ⇒ Python plugin loader disabled.
    /// Set <c>VAIS_PYTHON_PLUGINS_DIRECTORY</c> in the container environment to enable.
    /// Defaults to <see langword="null"/> (disabled) because Python is an opt-in runtime dependency.
    /// </summary>
    public string? PythonPluginsDirectory { get; init; }

    /// <summary>
    /// v0.xx Python plugin hot-reload policy. <see cref="ReloadPolicy.DrainAndSwap"/> registers
    /// <c>IPythonPluginReloader</c> and starts the filesystem watcher that restarts the
    /// Python subprocess on source changes without touching the .NET silo.
    /// Set <c>VAIS_PYTHON_PLUGINS_RELOAD_POLICY=DrainAndSwap</c> to enable.
    /// </summary>
    public ReloadPolicy PythonPluginsReloadPolicy { get; init; } = ReloadPolicy.Disabled;

    /// <summary>
    /// Container-plugins pillar. Directory scanned for container plugin subfolders (each containing
    /// <c>plugin.yaml</c> with <c>runtime: container</c>). Null or empty ⇒ container plugin
    /// host disabled. Set <c>VAIS_CONTAINER_PLUGINS_DIRECTORY</c> in the container environment
    /// to enable.
    /// </summary>
    public string? ContainerPluginsDirectory { get; init; }

    /// <summary>
    /// Plan C1 south cartridge. Optional directory scanned for <c>*.domain-ontology.json</c>
    /// artifacts at startup; each file registers under its filename stem and is bound to
    /// virtual MCP servers via <c>McpServerManifest.OntologyRef</c>. Null or empty ⇒ the
    /// in-memory registry stays empty (deployer can still register artifacts programmatically).
    /// Set via <c>VAIS_DOMAIN_ONTOLOGY_DIR</c>.
    /// </summary>
    public string? DomainOntologyDirectory { get; init; }

    /// <summary>
    /// Plan D — trajectory tee. Postgres connection for the trajectory store. When set, the
    /// runtime registers <c>PostgresInterceptorTeeStore</c> and replaces the in-memory
    /// ring-buffer default. Null / empty ⇒ in-memory (single-process) only.
    /// Set via <c>VAIS_INTERCEPTOR_TEE_STORE_CONNECTION</c>.
    /// </summary>
    public string? InterceptorTeeStoreConnection { get; init; }

    /// <summary>
    /// Plan D — induced recipes. Postgres connection for the recipe-proposal store. When set,
    /// the runtime registers <c>PostgresRecipeProposalStore</c> and replaces the in-memory
    /// default. Null / empty ⇒ in-memory (single-process) only.
    /// Set via <c>VAIS_RECIPE_PROPOSAL_STORE_CONNECTION</c>.
    /// </summary>
    public string? RecipeProposalStoreConnection { get; init; }

    /// <summary>
    /// Docker network name for internal-network isolation (Phase 2 egress isolation).
    /// When set, plugin containers join this network and are addressed via container-DNS;
    /// no host port is published. Null = legacy host-runtime mode.
    /// Set via <c>VAIS_DOCKER_PLUGIN_NETWORK</c> (e.g. <c>vais-internal</c>).
    /// </summary>
    public string? DockerPluginNetwork { get; init; }

    /// <summary>
    /// Lifetime in seconds of the short, renewable call tokens issued to session-mode container
    /// plugins (those with <c>spec.sessionTtlSeconds</c> set). Set via
    /// <c>VAIS_CONTAINER_PLUGIN_RENEW_TTL_SECONDS</c>. Default 120.
    /// </summary>
    public int ContainerPluginRenewTokenTtlSeconds { get; init; } = 120;

    /// <summary>
    /// Base URL of this runtime host as seen by Python plugin subprocesses.
    /// Python plugins call back to <c>{InternalGatewayBaseUrl}/v1/container-gateway/...</c>
    /// for LLM completions and tool invocations (P12 gateway contract).
    /// Must match the port in <c>ASPNETCORE_URLS</c>.
    /// Set via <c>VAIS_INTERNAL_GATEWAY_URL</c>; defaults to <c>http://localhost:8080</c>.
    /// </summary>
    public string InternalGatewayBaseUrl { get; init; } = "http://localhost:8080";

    /// <summary>
    /// v0.30 OIDC authority URL (e.g. <c>https://keycloak.example.com/realms/my-realm</c>).
    /// When set, the full JWT bearer-token authentication pipeline is wired on the runtime host.
    /// Null ⇒ auth pipeline disabled — existing localhost semantics unchanged.
    /// Set via <c>VAIS_JWT_AUTHORITY</c>.
    /// </summary>
    public string? JwtAuthority { get; init; }

    /// <summary>
    /// v0.30 optional token audience restriction. Applied only when <see cref="JwtAuthority"/> is set.
    /// Null ⇒ audience validation is disabled.
    /// Set via <c>VAIS_JWT_AUDIENCE</c>.
    /// </summary>
    public string? JwtAudience { get; init; }

    /// <summary>
    /// Whether the JWT bearer middleware requires the OIDC authority's metadata over HTTPS.
    /// Defaults to <see langword="true"/> (production-safe). Set <c>VAIS_JWT_REQUIRE_HTTPS_METADATA=false</c>
    /// only for local development / verification against an HTTP issuer (e.g. a local mock issuer).
    /// Applied only when <see cref="JwtAuthority"/> is set.
    /// </summary>
    public bool JwtRequireHttpsMetadata { get; init; } = true;

    /// <summary>
    /// v0.30 Kubernetes ServiceAccount principal mapper opt-in. When <see langword="true"/> and
    /// <see cref="JwtAuthority"/> is set, <c>ServiceAccountPrincipalMapper</c> is registered in
    /// preference to <c>DefaultPrincipalMapper</c> — extracts <c>TenantId</c> from the SA namespace
    /// in <c>system:serviceaccount:&lt;namespace&gt;:&lt;sa&gt;</c> sub claims.
    /// Set <c>VAIS_SA_PRINCIPAL_MAPPER=true</c> to enable.
    /// </summary>
    public bool UseSaPrincipalMapper { get; init; }

    /// <summary>
    /// Comma-separated list of allowed CORS origins (e.g. <c>http://localhost:5173</c>).
    /// In <c>localhost</c> mode, defaults to allowing all <c>localhost</c> / <c>127.0.0.1</c>
    /// origins so the Workbench dev server works without extra configuration.
    /// Set <c>VAIS_CORS_ORIGINS</c> to override; set to <c>disabled</c> to opt out entirely.
    /// </summary>
    public string? CorsOrigins { get; init; }

    /// <summary>
    /// Optional base URL of the Langfuse instance (e.g. <c>http://langfuse-web:3000</c>).
    /// When set, the startup self-check probes <c>{LangfuseHost}/api/health</c>.
    /// Set via <c>VAIS_LANGFUSE_HOST</c>.
    /// </summary>
    public string? LangfuseHost { get; init; }

    /// <summary>
    /// Postgres connection string for the run store (<see cref="Vais.Agents.Observability.RunStore.IRunStore"/>).
    /// When set, graph run history is persisted and exposed via the control-plane REST surface.
    /// Null ⇒ run store disabled (HTTP endpoints return 503).
    /// Set via <c>VAIS_RUN_STORE_CONNECTION</c>.
    /// </summary>
    public string? RunStoreConnection { get; init; }

    /// <summary>
    /// Postgres connection string for the agent run store. When set, standalone agent invocations
    /// are persisted and exposed via <c>GET /v1/agents/{id}/runs</c>.
    /// Null ⇒ store disabled (endpoint returns 503).
    /// Set via <c>VAIS_AGENT_RUN_STORE_CONNECTION</c>.
    /// </summary>
    public string? AgentRunStoreConnection { get; init; }

    /// <summary>
    /// Postgres connection string for the graph checkpointer. When set, the runtime persists graph-run
    /// checkpoints directly to Postgres (<c>PostgresGraphCheckpointer</c>) instead of the default
    /// Orleans-grain-backed checkpointer — a simpler, queryable storage option. Null ⇒ Orleans checkpointer
    /// (unchanged). Set via <c>VAIS_CHECKPOINTER_CONNECTION</c>.
    /// </summary>
    public string? CheckpointerConnection { get; init; }

    /// <summary>
    /// Path to a deployment-local ontology overlay JSON (Plan B). When set, the merged ontology
    /// (base + overlay) backs <c>vais.describe</c>, and any <c>authorRoles</c> in the overlay enable
    /// control-plane RBAC (mutating verbs are authorized per JWT scope). Null ⇒ base-only ontology,
    /// no RBAC (allow-all default). Set via <c>VAIS_ONTOLOGY_OVERLAY_PATH</c>.
    /// </summary>
    public string? OntologyOverlayPath { get; init; }

    /// <summary>
    /// Path to a JSON-Lines audit file (Plan B). When set, every control-plane lifecycle verb is
    /// appended as one JSON object per line. Null ⇒ the default <c>LoggerAuditLog</c>.
    /// Set via <c>VAIS_AUDIT_LOG_PATH</c>.
    /// </summary>
    public string? AuditLogPath { get; init; }

    /// <summary>
    /// Enables the human-in-the-loop approval queue for high-risk mutations (ContainerPlugin / Extension).
    /// When true, applying a high-risk kind returns <c>202 pending-approval</c> until an operator approves
    /// it via <c>vais approvals approve</c>. Default false (mutations proceed). Set via
    /// <c>VAIS_APPROVALS_ENABLED=true</c>.
    /// </summary>
    public bool ApprovalsEnabled { get; init; }

    /// <summary>
    /// Postgres connection string for the gateway event store. When set, LLM completion events
    /// are persisted and exposed via <c>GET /v1/llm-gateways/{id}/events</c>.
    /// Null ⇒ store disabled (endpoint returns 503).
    /// Set via <c>VAIS_GATEWAY_EVENT_STORE_CONNECTION</c>.
    /// </summary>
    public string? GatewayEventStoreConnection { get; init; }

    /// <summary>
    /// Gateway ID written into every <c>GatewayEvent</c> row. Must match the manifest
    /// <c>metadata.id</c> of the LLM gateway so the Workbench Events tab returns data.
    /// Defaults to <c>default</c> when unset.
    /// Set via <c>VAIS_GATEWAY_ID</c>.
    /// </summary>
    public string? GatewayId { get; init; }

    /// <summary>
    /// Postgres connection string for the MCP event store. When set, MCP tool-call events
    /// are persisted and exposed via <c>GET /v1/mcp-servers/{id}/events</c>.
    /// Null ⇒ store disabled (endpoint returns 503).
    /// Set via <c>VAIS_MCP_EVENT_STORE_CONNECTION</c>.
    /// </summary>
    public string? McpEventStoreConnection { get; init; }

    /// <summary>
    /// MCP server ID written into every <c>McpEvent</c> row. Must match the manifest
    /// <c>metadata.id</c> of the MCP server so the Workbench Tool Logs tab returns data.
    /// Defaults to <c>default</c> when unset.
    /// Set via <c>VAIS_MCP_SERVER_ID</c>.
    /// </summary>
    public string? McpServerId { get; init; }

    /// <summary>
    /// Postgres connection string for the MCP gateway event store. When set, MCP tool-call events
    /// routed through the MCP gateway are persisted and exposed via <c>GET /v1/mcp-gateways/{id}/events</c>.
    /// Null ⇒ store disabled (endpoint returns 503).
    /// Set via <c>VAIS_MCP_GATEWAY_EVENT_STORE_CONNECTION</c>.
    /// </summary>
    public string? McpGatewayEventStoreConnection { get; init; }

    /// <summary>
    /// MCP gateway ID written into every <c>McpGatewayEvent</c> row. Must match the manifest
    /// <c>metadata.id</c> of the MCP gateway config so the Workbench Tool Logs tab returns data.
    /// Defaults to <c>default</c> when unset.
    /// Set via <c>VAIS_MCP_GATEWAY_ID</c>.
    /// </summary>
    public string? McpGatewayId { get; init; }

    /// <summary>
    /// Maximum number of log lines retained per agent in the in-memory agent log sink.
    /// Oldest entries are evicted when the buffer is full.
    /// Defaults to 500. Set via <c>VAIS_AGENT_LOG_BUFFER_LINES</c>.
    /// </summary>
    public int AgentLogBufferLines { get; init; } = 500;

    /// <summary>
    /// Maximum seconds the host waits for Orleans grain drain and <see cref="IHostedService"/>
    /// shutdown to complete on SIGTERM. Must satisfy:
    /// <c>max grain drain ≤ ShutdownTimeoutSeconds &lt; external grace window</c>
    /// (compose <c>stop_grace_period</c> / K8s <c>terminationGracePeriodSeconds</c>).
    /// Default 30. Set via <c>VAIS_SHUTDOWN_TIMEOUT_SECONDS</c>.
    /// </summary>
    public int ShutdownTimeoutSeconds { get; init; } = 30;

    /// <summary>
    /// v0.xx Grain storage backend for <c>localhost</c> mode — controls <c>AiAgentGrain.StorageName</c>
    /// (the store used by every agent, registry, checkpoint, idempotency, and session grain).
    /// <see cref="LocalhostPersistenceMode.Postgres"/> makes API-deployed agents and graphs survive
    /// runtime restarts without requiring a manifest directory. Ignored in <c>clustered</c> mode.
    /// Requires <c>VAIS_POSTGRES_CONNECTION</c>. Set via <c>VAIS_LOCALHOST_PERSISTENCE=postgres</c>.
    /// </summary>
    public LocalhostPersistenceMode LocalhostPersistence { get; init; } = LocalhostPersistenceMode.Memory;

    /// <summary>
    /// Grain storage backend for the Orleans pub/sub store (<c>PubSubStore</c>) in
    /// <c>localhost</c> mode. Defaults to <see cref="LocalhostPersistenceMode.Memory"/>; set to
    /// <see cref="LocalhostPersistenceMode.Postgres"/> to make stream subscriptions durable.
    /// Independent of <see cref="LocalhostPersistence"/> — but Postgres pub-sub without Postgres
    /// main storage is an unusual combination. Ignored in <c>clustered</c> mode.
    /// Requires <c>VAIS_POSTGRES_CONNECTION</c>. Set via <c>VAIS_LOCALHOST_PUBSUB_PERSISTENCE=postgres</c>.
    /// </summary>
    public LocalhostPersistenceMode LocalhostPubSubPersistence { get; init; } = LocalhostPersistenceMode.Memory;

    /// <summary>
    /// Directory containing manifest files (YAML/JSON) applied to the registry on every runtime
    /// start. All five resource kinds are supported: <c>Agent</c>, <c>AgentGraph</c>,
    /// <c>LlmGatewayConfig</c>, <c>McpGatewayConfig</c>, <c>McpServer</c>. Files are processed
    /// in ordinal filename order; multi-document YAML (<c>---</c>) and mixed-kind files are
    /// supported (same format as <c>vais apply -f</c>). Null or empty ⇒ feature disabled.
    /// Non-existent directory logs a warning and is skipped.
    /// Set via <c>VAIS_BOOT_MANIFESTS_DIRECTORY</c>.
    /// </summary>
    public string? BootManifestsDirectory { get; init; }

    /// <summary>
    /// Opt-in in-process circular span buffer for <c>vais diagnose spans</c> / <c>vais diagnose trace</c>.
    /// Off by default — lossy, dev-only, single-silo; not safe for production.
    /// Set <c>VAIS_DIAG_SPAN_BUFFER=true</c> to enable.
    /// </summary>
    public bool DiagSpanBufferEnabled { get; init; }

    /// <summary>
    /// A2A cross-runtime graph-node invocation. Registers <c>IA2AGraphNodeInvoker</c>
    /// so graph nodes can delegate turns to agents on remote runtimes via the A2A protocol.
    /// Defaults to <see langword="true"/>. Set <c>VAIS_A2A_ENABLED=false</c> to disable.
    /// </summary>
    public bool A2aEnabled { get; init; } = true;

    /// <summary>
    /// PowerFx expression evaluator for graph edge conditions. When false,
    /// <c>IGraphExpressionEvaluator</c> is not registered and graph edges
    /// that reference PowerFx predicates will not be evaluated.
    /// Defaults to <see langword="true"/>. Set <c>VAIS_POWERFX_ENABLED=false</c> to disable.
    /// </summary>
    public bool PowerFxEnabled { get; init; } = true;

    /// <summary>
    /// HTTP control-plane idempotency middleware. When false, the idempotency store is not
    /// registered; the middleware no-ops gracefully (duplicate requests are not detected).
    /// Defaults to <see langword="true"/>. Set <c>VAIS_IDEMPOTENCY_ENABLED=false</c> to disable.
    /// </summary>
    public bool IdempotencyEnabled { get; init; } = true;

    /// <summary>
    /// Code-mode (the ScriptRuntime primitive). When true, agents with <c>spec.codeMode.enabled</c>
    /// execute LLM-authored scripts in the supervised sidecar at <see cref="ScriptRuntimeUrl"/>, and
    /// the container-gateway tool endpoints are mapped so scripts can call tools back through the
    /// gateway. Defaults to <see langword="false"/>. Set <c>VAIS_CODE_MODE_ENABLED=true</c> to enable.
    /// </summary>
    public bool CodeModeEnabled { get; init; }

    /// <summary>
    /// Base URL of the code-mode ScriptRuntime sidecar (co-deployed; <c>v1/script/run</c> is appended).
    /// Only used when <see cref="CodeModeEnabled"/>. Set <c>VAIS_SCRIPT_RUNTIME_URL</c> per topology
    /// (e.g. <c>http://script-runtime:8090</c> on compose, <c>http://localhost:8090</c> as a K8s pod sidecar).
    /// </summary>
    public string ScriptRuntimeUrl { get; init; } = "http://localhost:8090";

    /// <summary>Pull the canonical shape from process env vars.</summary>
    public static RuntimeOptions FromEnvironment()
    {
        return new RuntimeOptions
        {
            Mode = Env("VAIS_HOSTING_MODE") ?? DefaultMode,
            ClusteringBackend = Env("VAIS_CLUSTERING_BACKEND") ?? DefaultClusteringBackend,
            RedisConnection = Env("VAIS_REDIS_CONNECTION"),
            PostgresConnection = Env("VAIS_POSTGRES_CONNECTION"),
            StreamingBackend = Env("VAIS_STREAMING_BACKEND"),
            OtelEndpoint = Env("OTEL_EXPORTER_OTLP_ENDPOINT"),
            OtelHeaders = Env("OTEL_EXPORTER_OTLP_HEADERS"),
            OtelConsole = string.Equals(Env("VAIS_OTEL_CONSOLE"), "true", StringComparison.OrdinalIgnoreCase),
            LangfuseProject = Env("VAIS_LANGFUSE_PROJECT"),
            LangfuseHost = Env("VAIS_LANGFUSE_HOST"),
            OpaBaseUrl = Env("VAIS_OPA_BASEURL"),
            OpaFailMode = ParseFailMode(Env("VAIS_OPA_FAILMODE")),
            OpaDataPath = Env("VAIS_OPA_DATAPATH"),
            PluginsDirectory = PluginsEnv("VAIS_PLUGINS_DIRECTORY"),
            PluginsHotReload = ParseReloadPolicy(Env("VAIS_PLUGINS_RELOAD_POLICY")),
            PluginsDiagnoseUnloadLeaks = !string.Equals(Env("VAIS_PLUGINS_DIAGNOSE_UNLOAD_LEAKS"), "false", StringComparison.OrdinalIgnoreCase),
            PythonPluginsDirectory = Env("VAIS_PYTHON_PLUGINS_DIRECTORY"),
            PythonPluginsReloadPolicy = ParseReloadPolicy(Env("VAIS_PYTHON_PLUGINS_RELOAD_POLICY")),
            ContainerPluginsDirectory = Env("VAIS_CONTAINER_PLUGINS_DIRECTORY"),
            DomainOntologyDirectory = Env("VAIS_DOMAIN_ONTOLOGY_DIR"),
            InterceptorTeeStoreConnection = Env("VAIS_INTERCEPTOR_TEE_STORE_CONNECTION"),
            RecipeProposalStoreConnection = Env("VAIS_RECIPE_PROPOSAL_STORE_CONNECTION"),
            DockerPluginNetwork = Env("VAIS_DOCKER_PLUGIN_NETWORK"),
            ContainerPluginRenewTokenTtlSeconds =
                int.TryParse(Env("VAIS_CONTAINER_PLUGIN_RENEW_TTL_SECONDS"), out var renewTtl) ? renewTtl : 120,
            InternalGatewayBaseUrl = Env("VAIS_INTERNAL_GATEWAY_URL") ?? "http://localhost:8080",
            JwtAuthority = Env("VAIS_JWT_AUTHORITY"),
            JwtAudience = Env("VAIS_JWT_AUDIENCE"),
            JwtRequireHttpsMetadata = !string.Equals(Env("VAIS_JWT_REQUIRE_HTTPS_METADATA"), "false", StringComparison.OrdinalIgnoreCase),
            UseSaPrincipalMapper = string.Equals(Env("VAIS_SA_PRINCIPAL_MAPPER"), "true", StringComparison.OrdinalIgnoreCase),
            CorsOrigins = Env("VAIS_CORS_ORIGINS"),
            RunStoreConnection = Env("VAIS_RUN_STORE_CONNECTION"),
            AgentRunStoreConnection = Env("VAIS_AGENT_RUN_STORE_CONNECTION"),
            CheckpointerConnection = Env("VAIS_CHECKPOINTER_CONNECTION"),
            OntologyOverlayPath = Env("VAIS_ONTOLOGY_OVERLAY_PATH"),
            AuditLogPath = Env("VAIS_AUDIT_LOG_PATH"),
            ApprovalsEnabled = string.Equals(Env("VAIS_APPROVALS_ENABLED"), "true", StringComparison.OrdinalIgnoreCase),
            GatewayEventStoreConnection = Env("VAIS_GATEWAY_EVENT_STORE_CONNECTION"),
            GatewayId = Env("VAIS_GATEWAY_ID"),
            McpEventStoreConnection = Env("VAIS_MCP_EVENT_STORE_CONNECTION"),
            McpServerId = Env("VAIS_MCP_SERVER_ID"),
            McpGatewayEventStoreConnection = Env("VAIS_MCP_GATEWAY_EVENT_STORE_CONNECTION"),
            McpGatewayId = Env("VAIS_MCP_GATEWAY_ID"),
            AgentLogBufferLines = int.TryParse(Env("VAIS_AGENT_LOG_BUFFER_LINES"), out var bufLines) ? bufLines : 500,
            ShutdownTimeoutSeconds = int.TryParse(Env("VAIS_SHUTDOWN_TIMEOUT_SECONDS"), out var shutdownSecs) ? shutdownSecs : 30,
            BootManifestsDirectory = Env("VAIS_BOOT_MANIFESTS_DIRECTORY"),
            LocalhostPersistence = ParsePersistenceMode(Env("VAIS_LOCALHOST_PERSISTENCE")),
            LocalhostPubSubPersistence = ParsePersistenceMode(Env("VAIS_LOCALHOST_PUBSUB_PERSISTENCE")),
            DiagSpanBufferEnabled = string.Equals(Env("VAIS_DIAG_SPAN_BUFFER"), "true", StringComparison.OrdinalIgnoreCase),
            A2aEnabled         = !string.Equals(Env("VAIS_A2A_ENABLED"), "false", StringComparison.OrdinalIgnoreCase),
            PowerFxEnabled     = !string.Equals(Env("VAIS_POWERFX_ENABLED"), "false", StringComparison.OrdinalIgnoreCase),
            IdempotencyEnabled = !string.Equals(Env("VAIS_IDEMPOTENCY_ENABLED"), "false", StringComparison.OrdinalIgnoreCase),
            CodeModeEnabled = string.Equals(Env("VAIS_CODE_MODE_ENABLED"), "true", StringComparison.OrdinalIgnoreCase),
            ScriptRuntimeUrl = Env("VAIS_SCRIPT_RUNTIME_URL") ?? "http://localhost:8090",
        };

        static string? Env(string name)
        {
            var value = Environment.GetEnvironmentVariable(name);
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        // VAIS_PLUGINS_DIRECTORY distinguishes unset (use default) from empty (disabled).
        // `""` explicitly disables the loader; an unset var falls back to DefaultPluginsDirectory.
        static string? PluginsEnv(string name)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (value is null)
            {
                return DefaultPluginsDirectory;
            }
            return value;
        }

        static OpaFailMode ParseFailMode(string? raw) =>
            !string.IsNullOrWhiteSpace(raw) && Enum.TryParse<OpaFailMode>(raw, ignoreCase: true, out var parsed)
                ? parsed
                : OpaFailMode.Closed;

        static ReloadPolicy ParseReloadPolicy(string? raw) =>
            !string.IsNullOrWhiteSpace(raw) && Enum.TryParse<ReloadPolicy>(raw, ignoreCase: true, out var parsed)
                ? parsed
                : ReloadPolicy.Disabled;

        static LocalhostPersistenceMode ParsePersistenceMode(string? raw) =>
            string.Equals(raw, "postgres", StringComparison.OrdinalIgnoreCase)
                ? LocalhostPersistenceMode.Postgres
                : LocalhostPersistenceMode.Memory;
    }

    /// <summary>
    /// Validate mutually dependent fields. Throws <see cref="InvalidOperationException"/> with
    /// an actionable message when a required pairing is missing — for example, clustered mode
    /// without a connection string. Called before silo wiring so misconfiguration surfaces
    /// during the silo-builder callback, not at first grain invocation.
    /// </summary>
    public void EnsureValid()
    {
        if (ShutdownTimeoutSeconds <= 0)
            throw new InvalidOperationException("VAIS_SHUTDOWN_TIMEOUT_SECONDS must be > 0.");

        if (Mode is not ("localhost" or "clustered"))
        {
            throw new InvalidOperationException(
                $"VAIS_HOSTING_MODE must be 'localhost' or 'clustered'; got '{Mode}'.");
        }

        if (Mode == "localhost")
        {
            var needsPostgres = LocalhostPersistence == LocalhostPersistenceMode.Postgres
                             || LocalhostPubSubPersistence == LocalhostPersistenceMode.Postgres;
            if (needsPostgres && string.IsNullOrWhiteSpace(PostgresConnection))
            {
                throw new InvalidOperationException(
                    "VAIS_POSTGRES_CONNECTION is required when VAIS_LOCALHOST_PERSISTENCE or "
                    + "VAIS_LOCALHOST_PUBSUB_PERSISTENCE is set to 'postgres'. "
                    + "Set it to an Npgsql connection string and retry.");
            }
        }

        if (Mode == "clustered")
        {
            if (ClusteringBackend is not ("redis" or "postgres"))
            {
                throw new InvalidOperationException(
                    $"VAIS_CLUSTERING_BACKEND must be 'redis' or 'postgres' in clustered mode; got '{ClusteringBackend}'.");
            }

            if (ClusteringBackend == "redis" && string.IsNullOrWhiteSpace(RedisConnection))
            {
                throw new InvalidOperationException(
                    "VAIS_REDIS_CONNECTION is required when VAIS_HOSTING_MODE=clustered and VAIS_CLUSTERING_BACKEND=redis. "
                    + "Set it to a StackExchange.Redis connection string (e.g. 'redis:6379,password=...') and retry.");
            }

            if (ClusteringBackend == "postgres" && string.IsNullOrWhiteSpace(PostgresConnection))
            {
                throw new InvalidOperationException(
                    "VAIS_POSTGRES_CONNECTION is required when VAIS_HOSTING_MODE=clustered and VAIS_CLUSTERING_BACKEND=postgres. "
                    + "Set it to an Npgsql connection string and retry.");
            }

            if (StreamingBackend is not null
                && !string.Equals(StreamingBackend, "memory", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(StreamingBackend, "redis", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"VAIS_STREAMING_BACKEND must be 'memory' or 'redis'; got '{StreamingBackend}'.");
            }

            // A Redis stream provider needs a Redis connection — covers the default
            // (redis clustering) and the opt-in Postgres-clustering + Redis-streams pairing.
            if (EffectiveStreamingBackend == "redis" && string.IsNullOrWhiteSpace(RedisConnection))
            {
                throw new InvalidOperationException(
                    "VAIS_REDIS_CONNECTION is required when the event-bus stream provider is Redis "
                    + "(VAIS_STREAMING_BACKEND=redis, or the default when VAIS_CLUSTERING_BACKEND=redis). "
                    + "Set it to a StackExchange.Redis connection string and retry.");
            }
        }
    }
}
