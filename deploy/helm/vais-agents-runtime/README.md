# vais-agents-runtime

Helm chart for the Vais.Agents runtime (v0.16 Pillar A). Ships the Orleans-
backed HTTP control plane container as a single Deployment + Service, with
opt-in sidecars for OPA policy gating and knobs for OTel / Langfuse
observability.

> **Scope:** runtime install only. The separate
> [`vais-agents-operator`](../vais-agents-operator/README.md) chart ships the
> v0.13 Kubernetes operator for manifest reconciliation. The two charts are
> independently installable and do not depend on each other.

## TL;DR

```bash
# localhost mode — single-pod dev install, zero external deps.
helm install vais-runtime ./deploy/helm/vais-agents-runtime \
  --namespace vais --create-namespace

# clustered mode — 3 replicas against in-cluster Redis.
helm install vais-runtime ./deploy/helm/vais-agents-runtime \
  --namespace vais --create-namespace \
  --set hosting.mode=clustered \
  --set clustering.backend=redis \
  --set clustering.connectionString='redis.data.svc.cluster.local:6379' \
  --set replicaCount=3

# clustered + OPA sidecar with the default allow-all policy.
helm install vais-runtime ./deploy/helm/vais-agents-runtime \
  --namespace vais --create-namespace \
  --set hosting.mode=clustered \
  --set clustering.connectionString='redis.data.svc.cluster.local:6379' \
  --set opa.enabled=true
```

## Prerequisites

- Kubernetes 1.28+ (see `kubeVersion` in `Chart.yaml`).
- Helm 3.13+.
- A container registry reachable from the cluster holding the
  `vais-agents-runtime:0.16.0-preview` image, built from
  [`../../../src/Vais.Agents.Runtime.Host/Dockerfile`](../../../src/Vais.Agents.Runtime.Host/Dockerfile).
- **Clustered mode only:** a Redis 7 instance (single-node or Sentinel /
  Cluster) or a Postgres 14+ instance. The chart does **not** bundle either
  as a subchart — platform teams already have one, and bundling creates
  friction. Install [Bitnami Redis](https://github.com/bitnami/charts/tree/main/bitnami/redis)
  or your preferred managed option ahead of time.

## Image

Build locally for a kind / minikube cluster:

```bash
cd oss/agentic
docker build -t vais-agents-runtime:0.16.0-preview \
  -f src/Vais.Agents.Runtime.Host/Dockerfile .

# kind:
kind load docker-image vais-agents-runtime:0.16.0-preview --name <cluster>
```

Production installs push to a private registry and override `image.repository`.

## Values reference

| Key | Default | Description |
|---|---|---|
| `image.repository` | `vais-agents-runtime` | Container image repo. |
| `image.tag` | `0.16.0-preview` | Pinned to `appVersion`. |
| `image.pullPolicy` | `IfNotPresent` | |
| `replicaCount` | `1` | Must be 1 in localhost mode. |
| `hosting.mode` | `localhost` | `localhost` or `clustered`. |
| `clustering.backend` | `redis` | `redis` or `postgres`. Ignored in localhost mode. |
| `clustering.connectionString` | `""` | Required in clustered mode unless `existingSecret` is set. |
| `clustering.existingSecret` | `""` | Pre-existing Secret to pull connection string from. |
| `clustering.existingSecretKey` | `"connection-string"` | Key inside `existingSecret`. |
| `opa.enabled` | `false` | Off → `AllowAllPolicyEngine` (every verb allowed). |
| `opa.baseUrl` | `""` | External OPA URL; empty → pod-local sidecar. |
| `opa.image` | `openpolicyagent/opa:1.15.2` | Sidecar image. |
| `opa.failMode` | `Closed` | `Closed` = deny-on-failure; `Open` = allow. |
| `opa.dataPath` | `vais/agents/allow` | Rego data path queried by the adapter. |
| `opa.policy` | allow-all one-liner | Inline Rego rendered into an auto-generated ConfigMap. |
| `opa.configMapName` | `""` | Existing ConfigMap to mount instead of rendering. |
| `observability.otel.endpoint` | `""` | OTLP/gRPC endpoint. Off unless set. |
| `observability.otel.console` | `false` | Additionally emit to console (debug). |
| `observability.langfuse.project` | `""` | Langfuse project label. |
| `plugins.enabled` | `false` | Reserve `/var/lib/vais/plugins` mount (Pillar C uses this). |
| `plugins.persistentVolumeClaimName` | `""` | PVC to mount; empty → emptyDir. |
| `plugins.pythonReloadPolicy` | `""` | `""` disables hot-reload; `DrainAndSwap` enables the filesystem watcher and `POST /v1/plugins/{name}/source` endpoint. |
| `plugins.csharpReloadPolicy` | `""` | `""` disables hot-reload; `DrainAndSwap` enables the filesystem watcher and `POST /v1/plugins/{name}/dll` DLL-push endpoint. |
| `service.type` | `ClusterIP` | |
| `service.port` | `8080` | |
| `readinessProbe.failureThreshold` | `12` | 12 × 5s = 60s tolerance — tune up for larger Orleans clusters. |
| `resources.requests` | `cpu: 100m, memory: 256Mi` | |
| `resources.limits` | `memory: 512Mi` | No CPU limit by default (bursty silo traffic). |

Full surface in [`values.yaml`](./values.yaml) — every key there is
documented with inline comments.

## Three deploy patterns

### 1. Localhost mode (demo / dev)

Zero external deps; single pod. Exactly what `docker compose -f
docker-compose.localhost.yml up` does, in Kubernetes.

```bash
helm install vais-runtime . \
  --namespace vais --create-namespace
```

Grain state evaporates when the pod rolls — use clustered mode for any
workload that cares about durability.

### 2. Clustered + Redis

Standard production shape. Platform-team Redis (Bitnami chart, managed
cloud, or Sentinel) is the connection target; connection string lives in
a Secret.

```bash
kubectl create secret generic vais-redis \
  --namespace vais \
  --from-literal=connection-string='redis.data.svc.cluster.local:6379,password=...'

helm install vais-runtime . \
  --namespace vais \
  --set hosting.mode=clustered \
  --set clustering.backend=redis \
  --set clustering.existingSecret=vais-redis \
  --set replicaCount=3
```

All three durability sidecars (`OrleansTaskStore` / `OrleansCheckpointer` /
`OrleansIdempotencyStore`) engage automatically — the composition-root unit
tests guard the ordering discipline that makes them effective.

### 3. Clustered + OPA gating + OTel

Production shape with policy gating and trace export.

```bash
helm install vais-runtime . \
  --namespace vais \
  --set hosting.mode=clustered \
  --set clustering.existingSecret=vais-redis \
  --set replicaCount=3 \
  --set opa.enabled=true \
  --set-file opa.policy=./my-policy.rego \
  --set observability.otel.endpoint=http://otel-collector.monitoring.svc:4317
```

Want an external OPA instead of the sidecar? Set `opa.baseUrl` to the URL
and the chart skips the sidecar + ConfigMap:

```bash
--set opa.baseUrl=http://opa.opa-system.svc.cluster.local:8181
```

## Python plugin hot-reload on Kubernetes

Enable the reload policy so the runtime watches for source changes and exposes
the `POST /v1/plugins/{name}/source` push endpoint:

```bash
helm upgrade vais-runtime . \
  --namespace vais \
  --set plugins.enabled=true \
  --set plugins.pythonReloadPolicy=DrainAndSwap
```

The watcher is a no-op when nothing changes files — it is safe to enable in
production. Three delivery patterns are supported:

| Pattern | Description | Best for |
|---|---|---|
| `vais plugin push` | Pushes plugin source via the HTTP source endpoint; integrates cleanly with CI/CD pipelines | CI/CD-integrated workflows |
| ConfigMap volume | Kubernetes-managed sync (~60 s delay); mount the ConfigMap at `/var/lib/vais/plugins/<name>/` | Small plugins, no extra tooling |
| `git-sync` sidecar | Near-real-time sync from a git repository | Workflows with a git source-of-truth |

`vais plugin push` is the recommended CI/CD integration point — it targets the
runtime directly and does not require a pod restart or `kubectl exec`.

## C# plugin hot-reload on Kubernetes

Enable the reload policy so the runtime watches for DLL changes and exposes the
`POST /v1/plugins/{name}/dll` push endpoint:

```bash
helm upgrade vais-runtime . \
  --namespace vais \
  --set plugins.enabled=true \
  --set plugins.csharpReloadPolicy=DrainAndSwap
```

The watcher is a no-op when nothing changes files — it is safe to enable in
production. Two delivery patterns are supported:

| Pattern | Description | Best for |
|---|---|---|
| `vais plugin push --dll <file>` | Pushes a compiled DLL (or zip with deps) via the HTTP endpoint; ABI-validated before swap | CI/CD after `dotnet build` |
| `vais apply -f plugin.yaml` | Declarative manifest with embedded DLL reference; full P11 round-trip | GitOps workflows |

The runtime performs ABI pre-validation (PE header + handler type check) before
the swap. If validation fails the running plugin is untouched and an error is
returned — no partial state is possible.

## Security posture

- Runs as uid/gid 65532 (non-root, matches the Dockerfile).
- `readOnlyRootFilesystem: true` with a writable `/tmp` emptyDir; plugin
  mount is independent.
- Default `seccompProfile: RuntimeDefault`.
- OPA sidecar mirrors the same posture.
- **The default policy engine is allow-all.** Leaving `opa.enabled=false`
  means every control-plane verb is authorised; the startup log prints
  `opa=disabled (AllowAll)` so the behaviour is never silent, but a
  production install should either enable OPA or front the service with
  an auth-enforcing ingress.

## Known limitations (v0.16-preview)

- **Invoke returns 501.** Manifest-driven agent instantiation ships in
  Pillar B (v0.17). The Create / Get / Delete verbs + OpenAPI doc work
  today; `invoke` returns `501 urn:vais-agents:agent-not-instantiable`.
- **No Postgres streaming provider.** Clustered + Postgres degrades to
  in-silo memory streams (Orleans 10.x has no production Postgres stream
  provider yet). Logged at runtime startup; Redis clustering is the
  default for a reason.
- **No horizontal-scale autoscaler template.** Add your own HPA until the
  chart ships one; the probe shape (`/readyz` gated on silo-active) makes
  it safe to scale.
- **No image signing / SBOM / NetworkPolicy.** Pillar F polish.

## Related

- [`../../src/Vais.Agents.Runtime.Host/`](../../../src/Vais.Agents.Runtime.Host/)
  — source + Dockerfile.
- [`../../compose/`](../../compose/README.md) — docker-compose recipes
  (local dev equivalents).
- [`../vais-agents-operator/`](../vais-agents-operator/README.md) — v0.13
  Kubernetes operator chart, installed alongside for CRD-driven manifest
  reconciliation.
