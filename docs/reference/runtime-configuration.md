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
| `OTEL_EXPORTER_OTLP_PROTOCOL` | `grpc` | `grpc` \| `http/protobuf` | Standard OTel SDK env var. |
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

### Python agent settings (v0.24)

Additional env vars that apply when a plugin with `kind: agent-handler` is loaded:

| Env var | Default | Values | Notes |
|---|---|---|---|
| `VAIS_PYTHON_AGENT_MAX_STATE_BYTES` | `1048576` (1 MiB) | Positive integer, or `0` to disable | Maximum byte length (UTF-8) of the `newState` blob returned by `vais/agent.invoke`. Blobs that exceed this are rejected with `urn:vais-agents:python-agent-state-too-large`; the previous state is preserved. Set to `0` to disable the check (not recommended in production). |

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

## Composition-root decisions that can't change via config

These are baked in by the runtime host's composition root (see [`CompositionRoot.cs`](../../src/Vais.Agents.Runtime.Host/CompositionRoot.cs)) and would require a code change to alter:

- **Durability sidecars are always on.** `OrleansTaskStore` / `OrleansCheckpointer` / `OrleansIdempotencyStore` all register unconditionally, and they register *before* the generic HTTP control-plane wiring so the `TryAddSingleton` discipline picks the Orleans impls over the in-memory defaults. Unit tests guard the order.
- **`AddAgentControlPlaneOpenApi()` is always wired.** The v0.11 OpenAPI document is served at `/openapi/v1.json`.
- **`AddAgentControlPlaneIdempotency()` is always wired.** The idempotency middleware runs before the routes map; Orleans-backed store survives silo restart.
- **`AgentLifecycleManager` uses `OrleansAgentRegistry`** since v0.17 — manifests survive pod roll via per-id grain persistence.
- **Plugin loader registered before the manifest translator** since v0.18 — the translator's ctor queries `IPluginHandlerRegistry` lazily at build time. The composition root enforces this ordering; `Composition_Plugin_Registry_Registered_Before_Translator` locks it.

## Related

- [install-the-runtime-locally.md](../guides/install-the-runtime-locally.md) — docker-compose walkthrough.
- [deploy-the-runtime-to-kubernetes.md](../guides/deploy-the-runtime-to-kubernetes.md) — Helm walkthrough.
- [../concepts/architecture.md](../concepts/architecture.md) — where the Runtime.Host tier fits relative to the library layers.
- [../../deploy/helm/vais-agents-runtime/README.md](../../deploy/helm/vais-agents-runtime/README.md) — Helm values reference.
- [../../deploy/compose/README.md](../../deploy/compose/README.md) — docker-compose recipe index.
