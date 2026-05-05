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
    /// v0.23 Python-plugins pillar. Directory scanned for Python plugin subfolders (each containing
    /// <c>plugin.yaml</c> + <c>pyproject.toml</c>). Null or empty ⇒ Python plugin loader disabled.
    /// Set <c>VAIS_PYTHON_PLUGINS_DIRECTORY</c> in the container environment to enable.
    /// Defaults to <see langword="null"/> (disabled) because Python is an opt-in runtime dependency.
    /// </summary>
    public string? PythonPluginsDirectory { get; init; }

    /// <summary>
    /// v0.xx Python plugin hot-reload policy. <see cref="ReloadPolicy.DrainAndSwap"/> registers
    /// <see cref="IPythonPluginReloader"/> and starts the filesystem watcher that restarts the
    /// Python subprocess on source changes without touching the .NET silo.
    /// Set <c>VAIS_PYTHON_PLUGINS_RELOAD_POLICY=DrainAndSwap</c> to enable.
    /// </summary>
    public ReloadPolicy PythonPluginsReloadPolicy { get; init; } = ReloadPolicy.Disabled;

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
    /// Postgres connection string for the run store (<see cref="Vais.Agents.Observability.RunStore.IRunStore"/>).
    /// When set, graph run history is persisted and exposed via the control-plane REST surface.
    /// Null ⇒ run store disabled (HTTP endpoints return 503).
    /// Set via <c>VAIS_RUN_STORE_CONNECTION</c>.
    /// </summary>
    public string? RunStoreConnection { get; init; }

    /// <summary>
    /// v0.xx Grain storage backend for <c>localhost</c> mode — controls <see cref="AiAgentGrain.StorageName"/>
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

    /// <summary>Pull the canonical shape from process env vars.</summary>
    public static RuntimeOptions FromEnvironment()
    {
        return new RuntimeOptions
        {
            Mode = Env("VAIS_HOSTING_MODE") ?? DefaultMode,
            ClusteringBackend = Env("VAIS_CLUSTERING_BACKEND") ?? DefaultClusteringBackend,
            RedisConnection = Env("VAIS_REDIS_CONNECTION"),
            PostgresConnection = Env("VAIS_POSTGRES_CONNECTION"),
            OtelEndpoint = Env("OTEL_EXPORTER_OTLP_ENDPOINT"),
            OtelHeaders = Env("OTEL_EXPORTER_OTLP_HEADERS"),
            OtelConsole = string.Equals(Env("VAIS_OTEL_CONSOLE"), "true", StringComparison.OrdinalIgnoreCase),
            LangfuseProject = Env("VAIS_LANGFUSE_PROJECT"),
            OpaBaseUrl = Env("VAIS_OPA_BASEURL"),
            OpaFailMode = ParseFailMode(Env("VAIS_OPA_FAILMODE")),
            OpaDataPath = Env("VAIS_OPA_DATAPATH"),
            PluginsDirectory = PluginsEnv("VAIS_PLUGINS_DIRECTORY"),
            PluginsHotReload = ParseReloadPolicy(Env("VAIS_PLUGINS_RELOAD_POLICY")),
            PythonPluginsDirectory = Env("VAIS_PYTHON_PLUGINS_DIRECTORY"),
            PythonPluginsReloadPolicy = ParseReloadPolicy(Env("VAIS_PYTHON_PLUGINS_RELOAD_POLICY")),
            JwtAuthority = Env("VAIS_JWT_AUTHORITY"),
            JwtAudience = Env("VAIS_JWT_AUDIENCE"),
            UseSaPrincipalMapper = string.Equals(Env("VAIS_SA_PRINCIPAL_MAPPER"), "true", StringComparison.OrdinalIgnoreCase),
            CorsOrigins = Env("VAIS_CORS_ORIGINS"),
            RunStoreConnection = Env("VAIS_RUN_STORE_CONNECTION"),
            BootManifestsDirectory = Env("VAIS_BOOT_MANIFESTS_DIRECTORY"),
            LocalhostPersistence = ParsePersistenceMode(Env("VAIS_LOCALHOST_PERSISTENCE")),
            LocalhostPubSubPersistence = ParsePersistenceMode(Env("VAIS_LOCALHOST_PUBSUB_PERSISTENCE")),
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
        }
    }
}
