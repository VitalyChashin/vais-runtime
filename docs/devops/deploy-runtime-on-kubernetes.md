# Deploy the runtime on Kubernetes

You'll install the runtime Helm chart on a Kubernetes cluster, scale to 3 replicas against an in-cluster Redis, and exercise the control plane through `kubectl port-forward`. End state: `helm install` shows 3 healthy pods, `kubectl rollout status` is green, and `vais get` against `kubectl port-forward` works.

## Prerequisites

- `kubectl` and Helm 3.13+.
- Docker (for the image build).
- A cluster: [`kind`](https://kind.sigs.k8s.io/) for the dev-cluster path, or any 1.28+ cluster with a container registry and a Redis you can reach.

## What you'll end up with

```
┌─ vais namespace ────────────────────────────────────────────────┐
│                                                                 │
│  vais-runtime Deployment × 3 replicas                           │
│    └── each pod: runtime container + (optional) opa sidecar     │
│    └── /readyz gated on ISiloStatusOracle == SiloStatus.Active  │
│                                                                 │
│  vais-runtime Service (ClusterIP :8080)                         │
│  vais-runtime ServiceAccount                                    │
│  vais-runtime-opa-policy ConfigMap (only when opa.enabled)      │
│                                                                 │
└──────┬──────────────────────────────────────────────────────────┘
       │ VAIS_REDIS_CONNECTION (from Secret)
       ▼
  redis.data.svc.cluster.local:6379
  (platform-team Redis — not bundled by the chart)
```

## 1. Build and load the runtime image

The chart assumes a `vais-agents-runtime:local` image exists in a registry the cluster can pull from. On kind, side-load it directly from the local Docker daemon.

```bash
cd agentic

docker build \
  -t vais-agents-runtime:local \
  -f src/Vais.Agents.Runtime.Host/Dockerfile \
  .

# kind path:
kind create cluster --name vais
kind load docker-image vais-agents-runtime:local --name vais

# Remote-cluster path:
docker tag vais-agents-runtime:local registry.example.com/vais-agents-runtime:1.0.0
docker push registry.example.com/vais-agents-runtime:1.0.0
# ...then override image.repository + image.tag in the Helm install below.
```

## 2. Localhost mode — dev quickstart

Single-pod install, zero external deps. Useful for smoke-testing the chart and exercising the CLI without setting up Redis.

```bash
helm install vais-runtime ./deploy/helm/vais-agents-runtime \
  --namespace vais --create-namespace

kubectl rollout status deployment/vais-runtime -n vais
kubectl port-forward svc/vais-runtime 8080:8080 -n vais
```

In another terminal:

```bash
curl -s http://localhost:8080/healthz                # → {"status":"Healthy"}
curl -s http://localhost:8080/openapi/v1.json | head
```

Grain state evaporates on pod roll — use clustered mode for anything that needs durability.

## 3. Clustered mode — production shape

Three replicas against an in-cluster Redis. The chart deliberately does **not** bundle a Redis subchart — most orgs have a platform-team Redis, and bundling creates friction.

### 3.1 Install a Redis (if you don't have one)

```bash
helm repo add bitnami https://charts.bitnami.com/bitnami
helm install redis bitnami/redis \
  --namespace data --create-namespace \
  --set auth.enabled=false \
  --set architecture=standalone

kubectl rollout status statefulset/redis-master -n data
# → redis-master.data.svc.cluster.local:6379 is the connection target.
```

### 3.2 Store the connection string in a Secret

Never inline connection strings in `values.yaml` for production. The chart's `clustering.existingSecret` points at an existing Secret.

```bash
kubectl create namespace vais
kubectl create secret generic vais-redis -n vais \
  --from-literal=connection-string='redis-master.data.svc.cluster.local:6379'
```

For password-authenticated Redis: `redis-master.data.svc.cluster.local:6379,password=s3cret,ssl=false`.

### 3.3 Install the runtime

```bash
helm install vais-runtime ./deploy/helm/vais-agents-runtime \
  --namespace vais \
  --set hosting.mode=clustered \
  --set clustering.backend=redis \
  --set clustering.existingSecret=vais-redis \
  --set replicaCount=3
```

The install prints a NOTES banner with the current mode + OPA + OTel + Langfuse summary. Verify membership convergence:

```bash
kubectl rollout status deployment/vais-runtime -n vais
kubectl logs -n vais -l app.kubernetes.io/name=vais-agents-runtime --tail=-1 \
  | grep -E 'silo.*active|runtime starting'
# → Expect 3 silos reach SiloStatus.Active within ~60 s.
```

## 4. Opt in to OPA policy gating

Off by default. Flip it on and the chart runs an OPA sidecar in every pod with an inline Rego policy rendered into an auto-generated ConfigMap. A trivial allow-all ships as the default; real gating uses `--set-file opa.policy=...` or a pre-existing ConfigMap.

### 4.1 Sidecar OPA with your own policy

```bash
cat > my-policy.rego <<'EOF'
package vais.agents

default allow := {"allowed": false, "reason": "default-deny"}

allow := {"allowed": true} if {
    input.principal.tenantId == input.agent.labels.tenant
}
EOF

helm upgrade vais-runtime ./deploy/helm/vais-agents-runtime \
  --namespace vais \
  --reuse-values \
  --set opa.enabled=true \
  --set-file opa.policy=./my-policy.rego
```

The chart renders the policy into `vais-runtime-opa-policy` ConfigMap; the sidecar mounts it read-only at `/policies` and `opa run --server --watch` picks up edits.

### 4.2 External OPA (no sidecar)

```bash
helm upgrade vais-runtime ./deploy/helm/vais-agents-runtime \
  --namespace vais --reuse-values \
  --set opa.enabled=true \
  --set opa.baseUrl=http://opa.opa-system.svc.cluster.local:8181
```

The chart skips the sidecar + auto-generated ConfigMap in this path.

Richer policy samples live under [`samples/opa-policies/`](../../samples/opa-policies/). Deeper dive: [Gate agents with OPA](../guides/gate-agents-with-opa.md).

## 5. Wire observability

OTel + Langfuse are off by default. Existing collectors work without changing runtime code; the chart forwards endpoints via env vars.

```bash
helm upgrade vais-runtime ./deploy/helm/vais-agents-runtime \
  --namespace vais --reuse-values \
  --set observability.otel.endpoint=http://otel-collector.monitoring.svc.cluster.local:4317 \
  --set observability.langfuse.project=prod-agents
```

See **[Wire Langfuse](wire-langfuse.md)** and **[Wire Prometheus + Grafana](wire-prometheus-and-grafana.md)** for the full observability stack.

## 6. Exercise the runtime from the CLI

```bash
dotnet tool install -g Vais.Agents.Cli

kubectl port-forward svc/vais-runtime 8080:8080 -n vais &

vais config set-context k8s --server http://localhost:8080
vais config use-context k8s
vais version
vais apply -f agent.yaml
vais get
```

## 7. Teardown

```bash
helm uninstall vais-runtime -n vais
kubectl delete ns vais
# Platform-team Redis stays put.
```

## What you built

- A 3-replica runtime Deployment on Kubernetes, durable through pod rolls (Redis-backed clustering + grain storage).
- A ClusterIP Service the cluster can route to; `port-forward` for ad-hoc access.
- Optional OPA gating (sidecar or external), optional OTel + Langfuse wiring.
- A Helm-managed lifecycle — `helm upgrade --reuse-values` flips knobs without rebuilding.

## Known limitations

- No horizontal-pod-autoscaler template ships — add your own HPA targeting `app.kubernetes.io/name: vais-agents-runtime`; the `/readyz` gate makes scaling safe.
- Postgres clustering uses in-silo memory streams (no production Postgres stream provider in Orleans 10.x). Redis is the default for this reason.

## Next

- **[Add Redis persistence](add-redis-persistence.md)** — connection-string + Helm-secret patterns for production Redis.
- **[Add Postgres persistence](add-postgres-persistence.md)** — schema setup + connection wiring for Postgres-backed grain storage.
- **[Wire Langfuse](wire-langfuse.md)** — LLM-specific UI views layered on the OTel pipeline.
- [Reference → Runtime configuration](../reference/runtime-configuration.md) — every env var, `appsettings.json` knob, and Helm-values knob.
