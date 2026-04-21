# Reference: runtime configuration

Every knob on the `vais-agents-runtime` container (v0.16 Pillar A). Env vars are the authoritative surface; the Helm chart sets them via values; `appsettings.json` is the baked default for logging + Kestrel.

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
| `VAIS_POSTGRES_CONNECTION` | (unset) | Npgsql connection string | Required when `VAIS_HOSTING_MODE=clustered` and `VAIS_CLUSTERING_BACKEND=postgres`. Example: `Host=pg;Username=vais;Password=...;Database=orleans`. Clustering + grain storage only — streams degrade to in-silo memory (known limitation; Orleans 10.x has no production Postgres stream provider). |

Missing connection strings in clustered mode surface as an `InvalidOperationException` at startup with an actionable message — a test locks this in (`Options_Clustered_Mode_Requires_Connection_String`).

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
| `VAIS_LANGFUSE_PROJECT` | (unset) | string | When set, registers `LangfuseEnrichmentFilter` with a static `project` metadata tag. v0.16 writes the project label only; Pillar B expands to full Langfuse ingestion. |

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

v0.18 Pillar C. Loader scans the configured directory at first `IPluginHandlerRegistry` resolve; each subfolder is a plugin. See [runtime-plugins concept](../concepts/runtime-plugins.md) for the authoring contract + [package-an-agent-as-a-plugin guide](../guides/package-an-agent-as-a-plugin.md) for the end-to-end walkthrough.

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
- **`AgentLifecycleManager` uses `OrleansAgentRegistry`** since v0.17 Pillar B — manifests survive pod roll via per-id grain persistence. (v0.16 shipped `InMemoryAgentRegistry` because the 501-on-invoke limitation made durable registry outlive the feature.)
- **Plugin loader registered before the manifest translator** since v0.18 Pillar C — the translator's ctor queries `IPluginHandlerRegistry` lazily at build time. The composition root enforces this ordering; `Composition_Plugin_Registry_Registered_Before_Translator` locks it.

## Related

- [install-the-runtime-locally.md](../guides/install-the-runtime-locally.md) — docker-compose walkthrough.
- [deploy-the-runtime-to-kubernetes.md](../guides/deploy-the-runtime-to-kubernetes.md) — Helm walkthrough.
- [../concepts/architecture.md](../concepts/architecture.md) — where the Runtime.Host tier fits relative to the library layers.
- [../../deploy/helm/vais-agents-runtime/README.md](../../deploy/helm/vais-agents-runtime/README.md) — Helm values reference.
- [../../deploy/compose/README.md](../../deploy/compose/README.md) — docker-compose recipe index.
