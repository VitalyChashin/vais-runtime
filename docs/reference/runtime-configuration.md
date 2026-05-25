# Reference: runtime configuration

Every knob on the `vais-agents-runtime` container. Env vars are the authoritative surface; the Helm chart sets them via values; `appsettings.json` is the baked default for logging + Kestrel.

## Precedence

Higher wins — standard ASP.NET Core `Microsoft.Extensions.Configuration` layering:

1. Env vars (passed by Helm / docker-compose / `docker run -e`).
2. `appsettings.{ASPNETCORE_ENVIRONMENT}.json` (if present in the image or mounted via ConfigMap).
3. `appsettings.json` (baked into the image).

Helm values map 1:1 onto env vars — see the [chart README](../../deploy/helm/vais-agents-runtime/README.md) for the full table.

## Hosting mode

| Env var | Default | Values | Notes |
|---|---|---|---|
| `VAIS_HOSTING_MODE` | `localhost` | `localhost` \| `clustered` | Drives the Orleans silo configurator. `localhost` = `UseLocalhostClustering` + memory grain storage + memory streams (no external deps). `clustered` = requires a clustering connection string below. |
| `VAIS_CLUSTERING_BACKEND` | `redis` | `redis` \| `postgres` | Ignored in localhost mode. |
| `VAIS_REDIS_CONNECTION` | (unset) | StackExchange.Redis connection string | Required when `VAIS_HOSTING_MODE=clustered` and `VAIS_CLUSTERING_BACKEND=redis`. Example: `redis:6379,password=...,ssl=false`. Drives clustering, grain storage, and streaming. |
| `VAIS_POSTGRES_CONNECTION` | (unset) | Npgsql connection string | Required when (a) `VAIS_HOSTING_MODE=clustered` and `VAIS_CLUSTERING_BACKEND=postgres`, or (b) `VAIS_LOCALHOST_PERSISTENCE=postgres` or `VAIS_LOCALHOST_PUBSUB_PERSISTENCE=postgres` — see *Localhost persistence* below. Example: `Host=pg;Username=vais;Password=...;Database=orleans`. Clustered mode: clustering + grain storage only — streams degrade to in-silo memory (known limitation; Orleans 10.x has no production Postgres stream provider). |

Missing connection strings in clustered mode surface as an `InvalidOperationException` at startup with an actionable message — a test locks this in (`Options_Clustered_Mode_Requires_Connection_String`).

## Localhost persistence

Applies only when `VAIS_HOSTING_MODE=localhost`. By default both grain storage and pub-sub storage use in-process memory — fast and dependency-free, but state is lost on container restart. These vars swap in Postgres-backed ADO.NET grain storage without leaving localhost clustering mode.

| Env var | Default | Values | Notes |
|---|---|---|---|
| `VAIS_LOCALHOST_PERSISTENCE` | `memory` | `memory` \| `postgres` | Grain storage provider for `AiAgentGrain`. `memory` = ephemeral, zero deps. `postgres` = durable, requires `VAIS_POSTGRES_CONNECTION` and the Orleans schema (see below). |
| `VAIS_LOCALHOST_PUBSUB_PERSISTENCE` | `memory` | `memory` \| `postgres` | Grain storage provider for the Orleans `PubSubStore` (stream subscriptions). Defaults independently from grain storage. |

`VAIS_POSTGRES_CONNECTION` must be set when either var is `postgres`; the missing-connection check surfaces as an `InvalidOperationException` at startup (`Options_Localhost_Postgres_Requires_Connection_String` locks this in).

**Orleans schema prerequisite.** The Postgres grain storage provider does not auto-create the `OrleansStorage` table. Apply the schema once before the first start:

```sql
-- script is at: tests/Vais.Agents.Persistence.Postgres.Tests/Sql/PostgreSQL-Persistence.sql
-- PostgreSQL-Main.sql is not required in localhost mode — clustering stays in-memory.
psql -U <user> -d <db> -f PostgreSQL-Persistence.sql
```

The local dev `dev.ps1 start` applies this automatically against the shared `pgvector-db-1` container.

## OPA policy engine

Off-by-default. The runtime logs `opa=disabled (AllowAll)` on startup when nothing below is set; every control-plane verb is allowed.

| Env var | Default | Values | Notes |
|---|---|---|---|
| `VAIS_OPA_BASEURL` | (unset) | URL | When set, registers `OpaPolicyEngine` as the `IAgentPolicyEngine`. Otherwise the allow-all `NullAgentPolicyEngine.Instance` wins. Docker-compose sidecar example: `http://opa:8181`. |
| `VAIS_OPA_FAILMODE` | `Closed` | `Closed` \| `Open` | `Closed` = deny on OPA unreachable / timeout / malformed response (enterprise-safe). `Open` = allow. |
| `VAIS_OPA_DATAPATH` | `vais/agents/allow` | Rego package/rule | The path queried as `POST {BaseUrl}/v1/data/{DataPath}`. Match the `package` + rule name in your Rego. |

See [gate-agents-with-opa.md](../guides/gate-agents-with-opa.md) + [author-a-rego-policy-against-the-vais-input-schema.md](../guides/author-a-rego-policy-against-the-vais-input-schema.md) for policy authoring.

## Observability

Off-by-default. Zero overhead when nothing below is set.

| Env var | Default | Values | Notes |
|---|---|---|---|
| `OTEL_EXPORTER_OTLP_ENDPOINT` | (unset) | URL (gRPC) | When set, wires the OTLP exporter into both `TracerProviderBuilder` and `MeterProviderBuilder`. Pairs with `OTEL_EXPORTER_OTLP_PROTOCOL=grpc` (the default for the runtime). |
| `OTEL_EXPORTER_OTLP_PROTOCOL` | `grpc` | `grpc` \| `http/protobuf` | Standard OpenTelemetry SDK env var. The runtime does not read or override this — it is consumed directly by the OTel SDK when `OTEL_EXPORTER_OTLP_ENDPOINT` is set. Listed here for convenience. |
| `OTEL_SERVICE_NAME` | (unset) | string | Overrides the OTel resource `service.name`. When unset, the runtime advertises the assembly name. |
| `VAIS_OTEL_CONSOLE` | `false` | `true` \| `false` | Additionally emit traces + metrics to the console. Noisy — debug only. |
| `VAIS_LANGFUSE_PROJECT` | (unset) | string | When set, registers `LangfuseEnrichmentFilter` with a static `project` metadata tag. Writes the project label; deeper Langfuse ingestion is on the roadmap. |

Deeper dive: [deploy-otel-and-langfuse.md](../guides/deploy-otel-and-langfuse.md) + [telemetry-keys.md](./telemetry-keys.md).

## HTTP server

Standard ASP.NET Core Kestrel knobs. The baked `appsettings.json` binds Kestrel to `http://0.0.0.0:8080`; compose + Helm both override via `ASPNETCORE_URLS` for clarity.

| Env var | Default | Notes |
|---|---|---|
| `ASPNETCORE_URLS` | `http://0.0.0.0:8080` | Bind address(es). Multiple URLs are semicolon-separated. |
| `ASPNETCORE_ENVIRONMENT` | `Production` | Standard ASP.NET Core environment selector. |

## Health endpoints

Baked. Not configurable via env.

| Endpoint | Check | Notes |
|---|---|---|
| `GET /healthz` | Liveness — any non-crashed process passes | Returns 200 immediately after Kestrel binds. |
| `GET /readyz` | Readiness — `ISiloStatusOracle.GetApproximateSiloStatus(localSilo) == SiloStatus.Active` | Gates on Orleans silo join. In localhost mode this flips within ~5 s; in clustered mode, ~14 s P50 / ~30 s P99. |

Helm's default `readinessProbe.failureThreshold: 12 × periodSeconds: 5 = 60 s` gives 4× margin over the measured P99. Tune up for larger clusters via `--set readinessProbe.failureThreshold=N`.

## Plugin loader

Loader scans the configured directory at first `IPluginHandlerRegistry` resolve; each subfolder is a plugin. See [runtime-plugins concept](../concepts/runtime-plugins.md) for the authoring contract + [package-an-agent-as-a-plugin guide](../guides/package-an-agent-as-a-plugin.md) for the end-to-end walkthrough.

| Env var | Default | Values | Notes |
|---|---|---|---|
| `VAIS_PLUGINS_DIRECTORY` | `/var/lib/vais/plugins` | Absolute path, or empty string to disable | Unset ⇒ default. Explicit empty string (`VAIS_PLUGINS_DIRECTORY=""`) ⇒ loader disabled (no `IPluginHandlerRegistry` registered; translator skips the plugin branch). Missing / empty / unreadable directory ⇒ loader runs as no-op with a startup log entry. |

`appsettings.json` key: `Plugins:Directory`. The env var overrides per standard ASP.NET Core configuration layering.

**Startup log lines worth grepping for:**

- `Plugins directory '<path>' does not exist — plugin loading skipped.`
- `Plugins directory '<path>' is empty — no plugins to load.`
- `Loaded plugin '<name>' (targetApiVersion=<abi>, handlers=[...])`
- `Plugin loading complete — N plugin(s) loaded, M handler(s) registered.`

**Volume mount:** `/var/lib/vais/plugins` is exposed as a Docker `VOLUME`. The Helm chart's `plugins.{enabled,persistentVolumeClaimName}` values mount a PVC at this path.

## Python plugin loader

Opt-in — disabled by default because Python is an optional runtime dependency. See [polyglot-plugins concept](../concepts/polyglot-plugins.md) + [package-a-python-plugin guide](../guides/package-a-python-plugin.md).

| Env var | Default | Values | Notes |
|---|---|---|---|
| `VAIS_PYTHON_PLUGINS_DIRECTORY` | (unset) | Absolute path, or unset to disable | When set, registers `IPythonPluginHost` as a hosted service that scans the directory for Python plugin subfolders. Each subfolder must have a `plugin.yaml` with `spec.runtime: python` and a `pyproject.toml` with `[tool.vais.plugin]`. Unset or empty ⇒ Python plugin loader disabled. Missing / unreadable directory ⇒ loader runs as a no-op with a startup log entry. |

**Startup log lines worth grepping for:**

- `Python plugins directory '<path>' does not exist — python plugin loading skipped.`
- `Python plugin '<name>' loaded (pid=<N>, tools=[...], abi=0.23)`
- `Python plugin '<name>' failed ABI check — expected 0.23, got <X>. Skipped.`
- `Python plugin '<name>' handshake timed out after <N>s. Subprocess killed.`

**Volume mount:** The Python plugins directory should be mounted at the same path you pass to `VAIS_PYTHON_PLUGINS_DIRECTORY`. In the overlay Dockerfile pattern, plugin directories (including their `.venv/`) are baked into the image at the target path — no volume mount required.

`appsettings.json` key: `PythonPlugins:Directory`.

## Container plugin loader

Opt-in — disabled by default. Manages OCI-image plugins as Docker containers (local-dev) or Kubernetes Deployments (`vais plugin-deploy`). See [container-plugins concept](../concepts/container-plugins.md) and [harden-docker-container-plugins guide](../guides/harden-docker-container-plugins.md).

| Env var | Default | Values | Notes |
|---|---|---|---|
| `VAIS_CONTAINER_PLUGINS_DIRECTORY` | (unset) | Absolute path, or unset to disable | When set, registers `ContainerPluginHostService` as a hosted service. Filesystem-discovered plugins are loaded at startup; manifest-driven plugins flow through `vais apply -f` regardless of this setting. |
| `VAIS_DOCKER_PLUGIN_NETWORK` | (unset; legacy mode) | Docker network name (e.g. `vais-internal`) | When set, `DockerContainerSupervisor` attaches plugins to this network rather than publishing ports to `127.0.0.1`. Pair with `docker network create --internal vais-internal` to create the bridge once. P12 Phase 2 egress isolation; opt-in. |
| `VAIS_INTERNAL_GATEWAY_URL` | `http://localhost:8080` | URL | Base URL of this runtime host as seen by plugin subprocesses / containers. Plugins call back to `{InternalGatewayBaseUrl}/v1/container-gateway/...` (LLM + tool gateway) and `{InternalGatewayBaseUrl}/v1/otlp/v1/traces` (OTLP receiver). Must match the port the runtime binds. |

### Python agent settings (v0.24)

`PythonPluginLoaderOptions.MaxAgentStateSizeBytes` (default `1048576` / 1 MiB) caps the `newState` blob returned by `vais/agent.invoke`. Oversized blobs raise `urn:vais-agents:python-agent-state-too-large` and the previous state is preserved.

This setting is read from the `PythonPluginLoaderOptions` DI binding, not from an env var. To override, register a custom binding in the runtime composition root (`services.Configure<PythonPluginLoaderOptions>(o => o.MaxAgentStateSizeBytes = ...)`) or bind it from `appsettings.json` under `PythonPlugins:LoaderOptions:MaxAgentStateSizeBytes`. A dedicated `VAIS_PYTHON_AGENT_MAX_STATE_BYTES` env-var binding has not been wired through `RuntimeOptions.FromEnvironment()`.

`invokeTimeoutSeconds` is set per-plugin in `plugin.yaml` (`spec.health.invokeTimeoutSeconds`, default `60`) rather than as a global env var — different agent plugins may have different latency profiles.

**Startup log lines for agent-handler plugins:**

- `Python agent plugin '<name>' registered handler typeName '<typeName>' (pid=<N>).`
- `Python agent handler collision: typeName '<typeName>' already registered. Plugin '<name>' skipped.`

## Boot-apply manifests

Off-by-default. When set, the runtime applies all manifests in the directory on every start, before serving traffic.

| Env var | Default | Values | Notes |
|---|---|---|---|
| `VAIS_BOOT_MANIFESTS_DIRECTORY` | (unset) | Absolute path, or unset to disable | When set, `BootManifestApplyService` scans the directory (non-recursive) for `.yaml`, `.yml`, and `.json` files (sorted lexicographically) and applies each resource via the appropriate lifecycle manager: `AgentManifest`, `AgentGraphManifest`, `LlmGatewayConfig`, `McpGatewayConfig`, `McpServer`. Missing directory → startup warning + skip. Parse failure → `LogError` + continue. Same-version conflict (LLM/MCP gateway configs, MCP servers) → `LogDebug` + skip. |

**Idempotency.** Agent and agent-graph creates are upsert — safe on repeat restart. Gateway-config and MCP-server creates skip silently on duplicate `(id, version)`.

**Startup log lines worth grepping for:**

- `Boot-manifest directory '<path>' does not exist — skipping boot apply.`
- `Boot-manifest apply complete — N applied, M skipped (same version), K failed.`

## Graceful shutdown

Controls how long the host waits for Orleans grain drain to complete after SIGTERM before it is forcibly cancelled.

| Env var | Default | Notes |
|---|---|---|
| `VAIS_SHUTDOWN_TIMEOUT_SECONDS` | `30` | Maximum seconds `IHost.StopAsync` is allowed to run. Must satisfy: **max grain drain ≤ `VAIS_SHUTDOWN_TIMEOUT_SECONDS` < external grace window** (`stop_grace_period` in compose, `terminationGracePeriodSeconds` in K8s). The shipped defaults are 30 s host timeout + 45 s external grace — the 15 s gap is the SIGKILL safety net. |

**Shutdown contract.** On SIGTERM, the .NET generic host calls `IHost.StopAsync`, which triggers the Orleans silo's `Stopping` phase. Orleans deactivates all local grain activations, calls `OnDeactivateAsync` (reason `ShuttingDown`) on each, and waits for them to complete — all within `VAIS_SHUTDOWN_TIMEOUT_SECONDS`. Grain `OnDeactivateAsync` is **best-effort** — it is not called on SIGKILL, crash, or abnormal exit. Grain state durability does not depend on it (Orleans persists state independently); the shutdown drain is for session-lifecycle `closing` hooks and any deactivation-time cleanup.

**Startup log.** The active timeout value is emitted at boot:

```
Vais.Agents runtime starting — mode=localhost … shutdownTimeout=30s
```

**Diagnosis log.** When a grain drains on shutdown, it emits at Information level:

```
Grain deactivating on shutdown — agentId=<id>
```

This line confirms the drain reached the grain before the host budget expired. Its absence means the timeout was too short (raise `VAIS_SHUTDOWN_TIMEOUT_SECONDS`), or SIGTERM did not reach PID 1 (check the container entrypoint form — exec-form required).

See [Smoke test](../../deploy/shutdown-drain-test.ps1) for a verification script and the deploy docs for aligning external grace windows.

## Logging

The baked `appsettings.json` sets these log-levels:

```json
{
  "Logging": {
    "LogLevel": {
      "Default":                  "Information",
      "Microsoft.AspNetCore":     "Warning",
      "Microsoft.Hosting.Lifetime":"Information",
      "Orleans":                  "Information",
      "Vais.Agents":              "Information"
    }
  }
}
```

Override via `Logging__LogLevel__{Category}=Level` env vars or a mounted `appsettings.Production.json` ConfigMap.

## Optional integrations

All three default to `true` — existing deployments that do not set these vars see identical behaviour.

| Env var | Default | What it controls |
|---|---|---|
| `VAIS_A2A_ENABLED` | `true` | A2A cross-runtime graph-node invocation. When `false`, `IA2AGraphNodeInvoker` is not registered; graph nodes that reference remote A2A runtimes degrade gracefully (the invoker field arrives as `null`). |
| `VAIS_POWERFX_ENABLED` | `true` | PowerFx expression evaluator for graph edge conditions. When `false`, `IGraphExpressionEvaluator` is not registered; graph edges that reference PowerFx predicates are not evaluated. |
| `VAIS_IDEMPOTENCY_ENABLED` | `true` | HTTP control-plane idempotency middleware. When `false`, `IIdempotencyStore` is not registered and the middleware no-ops (duplicate control-plane requests are not detected). |

## OpenAI-compatible gateway routing flags

Applies when `AddOpenAiCompatGateway()` is used (library mode or the sample host). Bound from the `Vais:OpenAiCompat` configuration section — supports both `appsettings.json` and `Vais__OpenAiCompat__*` env vars.

| Config key / Env var | Default | What it controls |
|---|---|---|
| `Vais:OpenAiCompat:AgentRoutingEnabled` / `Vais__OpenAiCompat__AgentRoutingEnabled` | `true` | When `false`, `agent:*` models are excluded from `GET /v1/models` and `POST /v1/chat/completions` returns 404 for `agent:`-prefixed model IDs. |
| `Vais:OpenAiCompat:GraphRoutingEnabled` / `Vais__OpenAiCompat__GraphRoutingEnabled` | `true` | When `false`, `graph:*` models are excluded from `GET /v1/models` and `POST /v1/chat/completions` returns 404 for `graph:`-prefixed model IDs. |

Code override (applies after config-file binding, so code wins):

```csharp
builder.Services.AddOpenAiCompatGateway(o => o.AgentRoutingEnabled = false);
```

## Composition-root decisions that can't change via config

These are baked in by the runtime host's composition root (see [`CompositionRoot.cs`](../../src/Vais.Agents.Runtime.Host/CompositionRoot.cs)) and would require a code change to alter:

- **Durability sidecars are always on.** `OrleansTaskStore` / `OrleansCheckpointer` / `OrleansIdempotencyStore` all register unconditionally, and they register *before* the generic HTTP control-plane wiring so the `TryAddSingleton` discipline picks the Orleans impls over the in-memory defaults. Unit tests guard the order.
- **`AddAgentControlPlaneOpenApi()` is always wired.** The v0.11 OpenAPI document is served at `/openapi/v1.json`.
- **`AgentLifecycleManager` uses `OrleansAgentRegistry`** since v0.17 — manifests survive pod roll via per-id grain persistence.
- **Plugin loader registered before the manifest translator** since v0.18 — the translator's ctor queries `IPluginHandlerRegistry` lazily at build time. The composition root enforces this ordering; `Composition_Plugin_Registry_Registered_Before_Translator` locks it.

## Related

- [install-the-runtime-locally.md](../guides/install-the-runtime-locally.md) — docker-compose walkthrough.
- [deploy-the-runtime-to-kubernetes.md](../guides/deploy-the-runtime-to-kubernetes.md) — Helm walkthrough.
- [../concepts/architecture.md](../concepts/architecture.md) — where the Runtime.Host tier fits relative to the library layers.
- [../../deploy/helm/vais-agents-runtime/README.md](../../deploy/helm/vais-agents-runtime/README.md) — Helm values reference.
- [../../deploy/compose/README.md](../../deploy/compose/README.md) — docker-compose recipe index.
