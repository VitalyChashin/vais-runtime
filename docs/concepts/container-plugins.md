# Container plugins

The runtime can load plugins that ship as **OCI container images** rather than .NET assemblies (`runtime-plugins`) or Python subprocesses (`polyglot-plugins`). A container plugin is supervised by the runtime — started as a Docker container in local development or rolled out as a Kubernetes Deployment in production — and communicates with the runtime over an HTTP gateway protocol with HMAC-token auth, P12 sandbox hardening, and optional OTLP telemetry tunnelled back into the runtime's trace pipeline.

## Concept

Three plugin models coexist:

| Model | Process boundary | Communication | Concept doc |
|---|---|---|---|
| Assembly plugin | In-process (`AssemblyLoadContext` per plugin) | Direct method calls | [runtime-plugins](runtime-plugins.md) |
| Python plugin | Subprocess (stdio) | JSON-RPC / MCP | [polyglot-plugins](polyglot-plugins.md) / [polyglot-agents](polyglot-agents.md) |
| **Container plugin** | **Container (Docker / Pod)** | **HTTP over loopback or internal network** | **this doc** |

Container plugins are the right choice when the plugin needs its own OS, runtime, native deps, GPU, or non-Python / non-.NET language. The runtime cedes process supervision to the container runtime (Docker daemon or Kubernetes), but keeps full control of inbound message shaping (input middleware, guardrails) and outbound calls (LLM via `ILlmGateway`, tools via MCP Gateway). This split is the P12 plugin sandbox contract: container plugins are the topology where the contract is enforced most strictly — see the [P12 sandbox contract section](#p12-sandbox-contract) below.

```
┌─── .NET runtime ─────────────────────────────────────────────────────────┐
│                                                                          │
│  AiAgentGrain.AskAsync                                                   │
│       │                                                                  │
│       ├── input middleware chain                                         │
│       │                                                                  │
│       └→ ContainerAgentShim ──HTTP POST /invoke──→ container plugin      │
│                  ▲                                       │               │
│                  │ HTTP gateway (port 5001)              │               │
│                  ├── /v1/container-gateway/llm/complete  │               │
│                  ├── /v1/container-gateway/tools/*       │               │
│                  └── /v1/otlp/v1/traces  ←──OTLP spans───┘               │
│                                                                          │
└──────────────────────────────────────────────────────────────────────────┘
```

The plugin only opens HTTP connections to the runtime — never directly to providers (OpenAI, Anthropic) or MCP servers. Every external call exits through `ILlmGateway` / MCP Gateway, where the gateway middleware chain (rate limits, observability, policy, OPA) applies uniformly. This is the mandatory outbound side of P12.

## Surface area

`Vais.Agents.Runtime.Plugins.Container`:

| Type | Role |
|---|---|
| `ContainerPluginManifest` (in `Vais.Agents.Abstractions`) | Wire-format manifest — `apiVersion: vais.agents/v1`, `kind: ContainerPlugin`, `metadata`, `spec`. |
| `ContainerPluginDescriptor` | Runtime-internal projection of the manifest after secret resolution + DI lookup. Carries the image, port, topology, timeouts, resource limits, secret refs, and Phase-2 isolation flag. |
| `IContainerSupervisor` + `DockerContainerSupervisor` / `KubernetesContainerSupervisor` | Container lifecycle. Five-state machine (`Created` → `Starting` → `Ready` → `Stopping` → `Stopped`/`Failed`). Docker supervisor owns the `HostConfig` (P12 Phase 1 hardening); K8s supervisor delegates rollouts via `PATCH` on the Deployment. |
| `IContainerPluginHost` + `ContainerPluginHostService` | Manifest → descriptor projection, supervisor creation, registry-driven lifecycle hooks. Implements `IHostedService`. |
| `ContainerAgentShim` | The plugin-side adapter the agent grain holds; turns `AskAsync` / `StreamAsync` calls into HTTP requests to the container's `/invoke` endpoint and merges results back. |
| `IContainerPluginReloader` | Hot-reload entry point. Drains in-flight invocations, swaps the image, validates the handler type name didn't change. |
| `IContainerPluginRegistry` (in `Vais.Agents.Abstractions`) + `ContainerPluginRegistryGrain` | Orleans-backed registry. Same shape as `IAgentRegistry` — `ListAsync`, `GetAsync`, `RegisterAsync`, `RemoveAsync`. Survives silo restart. |
| `HmacCallTokenService` (`ICallTokenService`) | HMAC-SHA256 short-lived bearer tokens (`base64url(payload).base64url(hmac)`, payload `{runId}:{agentId}:{expiresAtUnixSeconds}`). Issues per-invocation tokens; `TryExtract` resolves the receiving side. |

## Manifest shape

```yaml
apiVersion: vais.agents/v1
kind: ContainerPlugin
metadata:
  id: research-pipeline
  version: "1.0.0"
  labels: { env: dev }
spec:
  image: ghcr.io/example/research-pipeline:1.0.0
  topology: standalone               # standalone | sidecar | kubernetes
  port: 8080
  startupTimeoutSeconds: 30
  invokeTimeoutSeconds: 60
  imagePullPolicy: IfNotPresent      # Always | IfNotPresent | Never
  retryPolicy:
    maxAttempts: 3
    backoffSeconds: 2
    retryOn: [502, 503, 504]
  secrets:
    OPENAI_API_KEY: secret://env/OPENAI_API_KEY
  build:                             # optional client-side build-on-apply
    context: ./
    dockerfile: Dockerfile
    buildArgs: { BASE_TAG: "1.0" }
    push: true
  kubernetes:                        # only when topology=kubernetes
    serviceUrl: http://research-pipeline.vais-agents.svc:8080
    deploymentName: research-pipeline
    namespace: vais-agents
```

`apply` flow: `vais apply -f plugin.yaml` → `POST /v1/container-plugins` → `IContainerPluginLifecycleManager.RegisterAsync` → `IContainerPluginRegistry.RegisterAsync` (durable) → `ContainerPluginHostService` projects the manifest into a `ContainerPluginDescriptor`, instantiates the matching `IContainerSupervisor`, calls `StartAsync`, waits for `/health` to return 200. Same shape as every other registry-backed manifest — fully `vais apply -f`-publishable per P11.

## CLI surface

| Command | Purpose |
|---|---|
| `vais plugin-init --runtime <python\|dotnet> --name <name> -o <dir>` | Scaffold `plugin.yaml` (+ `Dockerfile` for dotnet) with P12-safe defaults (`USER 65532:65532`, non-root). |
| `vais plugin-build --image <tag> --context <dir> [--push]` | `docker build -t <image> <context>`, optionally `docker push`. |
| `vais plugin-push <plugin-name\|image>` | Two modes: **source** (no `/` in arg) packs `./src` and `POST /v1/plugins/{name}/source` (hot-reload); **image** (arg contains `/` or `:`, or `--image` supplied) runs `docker push` and `POST /v1/plugins/{name}/image`. |
| `vais plugin-deploy <release> --image <tag> [...]` | Aggregate: `helm upgrade --install` against the built-in chart at `src/Vais.Agents.Cli/Charts/vais-plugin`, then `POST /v1/container-plugins`. Closes the P11 K8s gap in one command. |
| `vais plugin-status -o <table\|json\|yaml>` | `GET /v1/plugins`; fetches K8s replica counts for `topology: kubernetes` plugins. |
| `vais plugin-watch <name> --source <dir> --debounce <ms>` | Filesystem watch on `.py` / `.yaml` / `.toml` / `.json` / `.txt`, debounce, push on change. |

See [CLI concept](cli.md) for the full command surface.

## HTTP gateway protocol (port 5001)

The runtime exposes an **internal** gateway on a separate port (default 5001) that only plugins call. Mounted by `ContainerGatewayEndpointRouteBuilderExtensions.MapContainerGateway()` and `PluginOtlpEndpointRouteBuilderExtensions.MapPluginOtlp()`.

**Outbound for the plugin** (inbound for the runtime):

| Endpoint | Purpose |
|---|---|
| `POST /v1/container-gateway/llm/complete` | LLM completion. Accepts either `messages` or `sections` (per sectioned context composition), never both. |
| `POST /v1/container-gateway/chat/completions` | OpenAI-compatible chat variant. |
| `POST /v1/container-gateway/tools/invoke` | Invoke a registered tool via the MCP Gateway middleware chain. |
| `GET /v1/container-gateway/tools/list` | Enumerate the agent's tool registry. |
| `POST /v1/container-gateway/sections/build` | Build a sectioned request via the runtime-side composer. |
| `POST /v1/otlp/v1/traces` | OTLP/protobuf trace receiver. See [OTLP telemetry](#otlp-telemetry). |

**Auth** uses two related schemes against the same `HmacCallTokenService`:

- **Gateway calls** — `Authorization: Bearer <token>` plus `X-Run-Id` / `X-Agent-Id` headers. The runtime validates `(token, runId, agentId)` per call.
- **OTLP receiver** — `Authorization: vais-plugin-token <token>` (single header). The receiver calls `TryExtract(token, out runId, out agentId)` to recover the trace context.

Tokens are HMAC-SHA256-signed, short-lived (`invokeTimeoutSeconds + 30s` for gateway calls; 24 h for OTLP), and minted per invocation by the supervisor. The signing key comes from `Vais:ContainerPlugin:CallTokenSecret` (min 32 chars; required in production configuration).

**Environment variables injected by the supervisor** at container start:

| Var | Purpose |
|---|---|
| `VAIS_RUNTIME_INTERNAL_URL` | Base URL of the internal gateway (e.g. `http://localhost:5001` on Docker, in-cluster service URL on K8s). |
| `VAIS_PLUGIN_TOKEN` | Bearer token for gateway calls. |
| `VAIS_RUN_ID` / `VAIS_AGENT_ID` | Correlation IDs for `X-Run-Id` / `X-Agent-Id`. |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | OTLP receiver URL (e.g. `http://localhost:5001/v1/otlp`). Set only when OTLP is enabled. |
| `OTEL_EXPORTER_OTLP_PROTOCOL` | Always `http/protobuf` when OTLP is enabled. |
| `OTEL_EXPORTER_OTLP_HEADERS` | `Authorization=vais-plugin-token <token>`. |
| `OTEL_RESOURCE_ATTRIBUTES` | `vais.agent_id=<plugin-name>`. |

The Python SDK (`vais-plugin`) reads these on import and auto-configures the OpenTelemetry SDK when the optional `vais-plugin[otlp]` extra is installed (silently no-ops otherwise).

## P12 sandbox contract

Container plugins are the strictest enforcement point of the P12 plugin sandbox contract. Both topologies ship hardening defaults; on K8s the defaults are negotiable via Helm values, but on Docker they are non-negotiable.

### Phase 1 — Docker hardening (always on)

Applied by `DockerContainerSupervisor.BuildHostConfig()`:

- `ReadonlyRootfs = true` — root filesystem read-only.
- `Tmpfs = { "/tmp": "rw,size=64m,mode=1777" }` — writable tmp with size cap.
- `CapDrop = ["ALL"]` — every Linux capability dropped.
- `SecurityOpt = ["no-new-privileges:true"]` — no `setuid` escalation.
- Resource limits — `Memory`, `MemorySwap`, `NanoCPUs`, `PidsLimit` (defaults 256 MiB / 0.5 CPU / 128 PIDs).
- Host port binding pinned to `127.0.0.1` (legacy mode) — never exposed on the host's external interfaces.

### Phase 2 — egress isolation (opt-in)

Set `VAIS_DOCKER_PLUGIN_NETWORK=vais-internal` in the runtime environment and the supervisor switches to `HostConfig.NetworkMode = "vais-internal"`, drops `PortBindings` entirely, and reaches plugins via Docker DNS (`vais-plugin-<name>`). The internal bridge has no NAT path to the host or internet — outbound calls must round-trip through the runtime gateway, which is the entire point of P12. Create the bridge once: `docker network create --internal vais-internal`.

### Kubernetes equivalents

The built-in `vais-plugin` Helm chart applies:

- `podSecurityContext` — `runAsNonRoot: true`, `runAsUser: 65532`, `runAsGroup: 65532`, `fsGroup: 65532`, `seccompProfile: RuntimeDefault`.
- `securityContext` — `allowPrivilegeEscalation: false`, `readOnlyRootFilesystem: true`, `capabilities.drop: ["ALL"]`.
- `NetworkPolicy` (enabled by default) — ingress only from the runtime Service; egress only to the runtime + kube-dns + the OTLP port when enabled.
- RBAC — `rbac.pluginSupervision` grants the runtime ServiceAccount `get` + `patch` on `apps/v1/deployments` so `KubernetesContainerSupervisor` can roll out new images.

## OTLP telemetry

`OPENAI`-style trace data from a plugin would normally exit to a remote OTel collector. Container plugins re-route through the runtime: the plugin sends OTLP/protobuf to `POST /v1/otlp/v1/traces` (loopback or internal bridge), and `OtlpSpanForwarder` re-emits each span as a .NET `System.Diagnostics.Activity` under `ActivitySource("Vais.Agents.Runtime.Plugins.Container.Otlp")`. The runtime's own OTel pipeline picks them up — `AddAgenticInstrumentation()` subscribes the source automatically — so plugin spans land alongside the surrounding graph-node span in Langfuse, Tempo, or whichever backend the runtime targets.

The plugin never opens a direct connection to Langfuse / Tempo / Jaeger. The runtime is the single egress path for telemetry, exactly as it is for LLM and tool calls. See [observability concept](observability.md).

## Topology modes

**Docker (local-dev)** — `local-dev/dev.ps1` starts the runtime + plugin containers on the default bridge or the Phase-2 internal bridge. `DockerContainerSupervisor` handles drain-and-replace for hot reload (waits for `_activeInvokes` to reach 0, stops the container, swaps the image, restarts). `tests/e2e/docker/run.ps1` runs the containerised E2E suite.

**Kubernetes (standalone)** — `KubernetesContainerSupervisor` issues a `PATCH apps/v1/namespaces/{ns}/deployments/{name}` to change the image and returns `RolloutStarted` immediately; Kubernetes handles the rolling update. The built-in Helm chart lives at `src/Vais.Agents.Cli/Charts/vais-plugin/`. `tests/e2e/k8s/` covers the K8s topology with `imagePullPolicy: Never` against Docker Desktop.

The topology is chosen per plugin via `spec.topology`, not per runtime — one runtime can supervise a mix of Docker and Kubernetes plugins as long as both supervisors are registered in DI.

## Hot reload

Source mode (Python only): `vais plugin-push <name>` packs `./src`, uploads to `POST /v1/plugins/{name}/source`; runtime extracts into an overlay directory and reloads the module without restarting the container.

Image mode: `vais plugin-push <name>:<tag>` runs `docker push`, then `POST /v1/plugins/{name}/image`. `IContainerPluginReloader.ReloadAsync` drains in-flight invocations, stops the container, updates the descriptor's image field, restarts, and validates the handler type name is unchanged. If the new image exports a different handler type, the reloader returns `HandlerTypeNameChanged` — a silo restart is required to pick up the new contract.

K8s rollout: same endpoint, but the supervisor returns `RolloutStarted` and Kubernetes drives the rolling update via Deployment surge / max-unavailable semantics.

## Failure URNs

`ContainerPluginUrns`:

| URN | Meaning |
|---|---|
| `urn:vais:container:startup-timeout` | Container did not reach `Ready` within `startupTimeoutSeconds`. |
| `urn:vais:container:health-check-failed` | `/health` returned non-2xx after startup. |
| `urn:vais:container:abi-failed` | Handler type name or metadata mismatch at handshake. |
| `urn:vais:container:handler-type-name-changed` | New image's handler type differs — silo restart required. |
| `urn:vais:container:invoke-network-error` | HTTP transport error reaching the plugin. |
| `urn:vais:container:invoke-failed` | Plugin returned a non-2xx response to `/invoke`. |
| `urn:vais:container:opaque-state-deserialization-error` | State blob the plugin returned could not be parsed. |
| `urn:vais:container:system-prompt-resolution-failed` | Template lookup failed during preprocessing. |
| `urn:vais:container:no-supervisor` | No `IContainerSupervisor` is registered for the plugin's topology. |
| `urn:vais:container:start-failed` | Docker or Kubernetes start API returned an error. |

All surface as HTTP Problem Details when the runtime's control plane reports them.

## Related

- [Runtime plugins concept](runtime-plugins.md) — the .NET assembly plugin model and the full P12 contract.
- [Polyglot plugins concept](polyglot-plugins.md) — Python subprocess plugins (tool-scope only).
- [Polyglot agents concept](polyglot-agents.md) — Python agent shim for full-agent handlers.
- [CLI concept](cli.md) — every `vais plugin-*` command.
- [Observability concept](observability.md) — how plugin OTLP spans join the runtime trace.
- [Architecture concept](architecture.md) — package layering and the agent-input-middleware seam.
