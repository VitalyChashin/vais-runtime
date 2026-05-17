# Guide: deploy the runtime to Kubernetes

End-to-end walkthrough from an empty kind cluster to a 3-replica `vais-agents-runtime` Deployment serving the v0.11 OpenAPI spec through a ClusterIP service. Built on the Helm chart that ships under [`deploy/helm/vais-agents-runtime/`](../../deploy/helm/vais-agents-runtime/README.md).

Prereqs: `kubectl`, Helm 3.12+, Docker (for the image build). [`kind`](https://kind.sigs.k8s.io/) for the dev-cluster path, or any 1.28+ cluster with a container registry and a Redis you can reach.

## What we'll end up with

```
┌─ vais namespace ────────────────────────────────────────────────┐
│                                                                 │
│  vais-runtime Deployment × 3 replicas                           │
│    └── each pod: runtime container + (optional) opa sidecar     │
│    └── /readyz gated on ISiloStatusOracle == SiloStatus.Active  │
│                                                                 │
│  vais-runtime Service (ClusterIP :8080)                         │
│                                                                 │
│  vais-runtime ServiceAccount                                    │
│  vais-runtime-opa-policy ConfigMap  (only when opa.enabled)     │
│                                                                 │
└──────┬──────────────────────────────────────────────────────────┘
       │ VAIS_REDIS_CONNECTION (from Secret)
       ▼
  redis.data.svc.cluster.local:6379
  (platform-team Redis — not bundled)
```

## 1. Build + load the runtime image

Helm assumes the `vais-agents-runtime:0.16.0-preview` image exists in a registry the cluster can pull from. On kind, side-load it directly from the local Docker daemon.

```bash
cd oss/agentic

docker build \
  -t vais-agents-runtime:0.16.0-preview \
  -f src/Vais.Agents.Runtime.Host/Dockerfile \
  .

# kind:
kind create cluster --name vais
kind load docker-image vais-agents-runtime:0.16.0-preview --name vais

# Remote cluster:
docker tag vais-agents-runtime:0.16.0-preview registry.example.com/vais-agents-runtime:0.16.0-preview
docker push registry.example.com/vais-agents-runtime:0.16.0-preview
# ...then override image.repository in the Helm install below.
```

## 2. Localhost mode — dev quickstart

Single-pod install, zero external deps. Useful for smoke-testing the chart and exercising the CLI against a cluster-hosted runtime without setting up Redis.

```bash
helm install vais-runtime ./deploy/helm/vais-agents-runtime \
  --namespace vais --create-namespace

kubectl rollout status deployment/vais-runtime -n vais
kubectl port-forward svc/vais-runtime 8080:8080 -n vais

# Another terminal:
curl -s http://localhost:8080/healthz               # → {"status":"Healthy"}
curl -s http://localhost:8080/openapi/v1.json | head
```

Grain state evaporates on pod roll — use clustered mode for anything that cares about durability.

## 3. Clustered mode — production shape

Three replicas against an in-cluster Redis. The chart deliberately does **not** bundle a Redis subchart — platform-team Redis is common, and bundling creates friction for orgs that already have one.

### 3.1 Install a Redis (if you don't have one)

```bash
helm repo add bitnami https://charts.bitnami.com/bitnami
helm install redis bitnami/redis \
  --namespace data --create-namespace \
  --set auth.enabled=false \
  --set architecture=standalone
```

Wait for it to converge:

```bash
kubectl rollout status statefulset/redis-master -n data
# → redis-master.data.svc.cluster.local:6379 is the connection target.
```

### 3.2 Store the connection string in a Secret

Never inline connection strings in `values.yaml` for production. The chart's `clustering.existingSecret` lets you point at an existing Secret.

```bash
kubectl create namespace vais
kubectl create secret generic vais-redis -n vais \
  --from-literal=connection-string='redis-master.data.svc.cluster.local:6379'
```

For password-authenticated Redis, embed the password in the connection string per the StackExchange.Redis syntax: `redis-master.data.svc.cluster.local:6379,password=s3cret,ssl=false`.

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
  | grep -E 'silo.*active|Vais\.Agents runtime starting'
# → Expect 3 silos reach SiloStatus.Active within ~60 s.
```

All three durability sidecars (`OrleansTaskStore` / `OrleansCheckpointer` / `OrleansIdempotencyStore`) engage automatically — the composition-root unit tests guard the ordering discipline.

## 4. Opt in to OPA policy gating

Off-by-default. When you flip it on without a `baseUrl`, the chart runs an OPA sidecar in every pod with an inline Rego policy rendered into an auto-generated ConfigMap. A trivial allow-all ships as the default; real gating uses `--set-file opa.policy=...` or a pre-existing ConfigMap.

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

Helm renders the policy into `vais-runtime-opa-policy` ConfigMap; the sidecar mounts it read-only at `/policies` and `opa run --server --watch` picks up edits (relevant if you swap to a pre-existing ConfigMap and do in-place edits).

### 4.2 External OPA (no sidecar)

Already running OPA as a platform service? Point at it:

```bash
helm upgrade vais-runtime ./deploy/helm/vais-agents-runtime \
  --namespace vais --reuse-values \
  --set opa.enabled=true \
  --set opa.baseUrl=http://opa.opa-system.svc.cluster.local:8181
```

The chart skips the sidecar + auto-generated ConfigMap in this path.

Richer policy samples live under [`samples/opa-policies/`](../../samples/opa-policies/). Deeper dive: [gate-agents-with-opa.md](./gate-agents-with-opa.md).

## 5. Wire observability

OTel + Langfuse are off by default — zero overhead until a dev opts in. Existing collectors work without changing runtime code; the chart just forwards the endpoints via env vars.

### 5.1 OTLP to a collector

```bash
helm upgrade vais-runtime ./deploy/helm/vais-agents-runtime \
  --namespace vais --reuse-values \
  --set observability.otel.endpoint=http://otel-collector.monitoring.svc.cluster.local:4317
```

The runtime emits traces + metrics from both instrumentation sources registered by `AddAgenticInstrumentation`. Deeper dive: [deploy-otel-and-langfuse.md](./deploy-otel-and-langfuse.md).

### 5.2 Langfuse project tag

```bash
helm upgrade vais-runtime ./deploy/helm/vais-agents-runtime \
  --namespace vais --reuse-values \
  --set observability.langfuse.project=prod-agents
```

The chart wires the project label through the Langfuse enrichment filter. Deeper enrichment (full trace ingestion with auth) is on the roadmap.

## 6. Exercise the runtime from the CLI

```bash
dotnet tool install -g Vais.Agents.Cli

kubectl port-forward svc/vais-runtime 8080:8080 -n vais &

vais config set base-url http://localhost:8080
vais version
vais apply -f agent.yaml
vais get agents
```

`vais invoke` against a manifest with `handler.typeName: declarative` returns the model response once a `Model` block is set; manifests without one and without a matching plugin return `501 urn:vais-agents:handler-not-loaded` — see [author-an-agent-in-yaml](./author-an-agent-in-yaml.md) for the declarative-path walkthrough.

## 7. Teardown

```bash
helm uninstall vais-runtime -n vais
kubectl delete ns vais
# Platform-team Redis stays put.
```

## Known limitations

- **Postgres clustering** uses in-silo memory streams (no production Postgres stream provider in Orleans 10.x). Redis is the default for this reason.
- **No horizontal-pod-autoscaler template.** Add your own HPA targeting `app.kubernetes.io/name: vais-agents-runtime`; the `/readyz` gate makes scaling safe.
- **No NetworkPolicy / image signing / SBOM** — on the roadmap.
- **Kind integration isn't CI-gated** — the runtime tier relies on the composition-root unit tests + `helm lint` + template renders to prove structural invariants. A kind harness may arrive later if partners hit regressions.

## Next

- [../reference/runtime-configuration.md](../reference/runtime-configuration.md) — full env-var / appsettings / Helm-values cross-reference.
- [install-the-runtime-locally.md](./install-the-runtime-locally.md) — docker-compose recipes for pre-Kubernetes evaluation.
- [deploy-the-kubernetes-operator.md](./deploy-the-kubernetes-operator.md) — install the v0.13 operator alongside for CRD-driven manifest reconciliation.
