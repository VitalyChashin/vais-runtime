# KubernetesOperatorQuickstart

Deploy the Vais.Agents runtime and operator on Docker Desktop Kubernetes. Apply `sample-agent.yaml`, watch the operator reconcile the Agent CR into a registered agent, then clean up.

> **Doc-only sample.** No C# to build; all steps are `helm` + `kubectl` + `vais` commands.

## Prerequisites

- Docker Desktop with Kubernetes enabled (`Settings > Kubernetes > Enable Kubernetes`).
- `helm` ≥ 3.14 and `kubectl` on `PATH`.
- `vais` CLI installed: `dotnet tool install -g Vais.Agents.Cli`.
- A local build of the runtime image — see [build instructions](../../deploy/README.md) — or replace `image.tag` with a registry pull tag.

## Steps

### 1 — Install the runtime

```bash
helm install vais-agents-runtime ./deploy/helm/vais-agents-runtime \
  --namespace vais-agents --create-namespace \
  --set image.tag=0.15.0-preview \
  --set image.pullPolicy=IfNotPresent \
  --set hosting.mode=localhost
```

Wait for ready:

```bash
kubectl rollout status deployment/vais-agents-runtime -n vais-agents
# deployment "vais-agents-runtime" successfully rolled out
```

### 2 — Install the operator

```bash
helm install vais-agents-operator ./deploy/helm/vais-agents-operator \
  --namespace vais-agents \
  --set controlPlane.baseUrl=http://host.docker.internal:5080 \
  --set image.tag=0.13.0-preview \
  --set image.pullPolicy=IfNotPresent
```

### 3 — Configure a local context

```bash
vais config set-context local --server http://localhost:5080 --token dev-token
vais config use-context local
```

### 4 — Apply the sample agent CR

```bash
kubectl apply -f samples/KubernetesOperatorQuickstart/sample-agent.yaml
# agent.vais.io/weather created
```

The operator detects the new `Agent` CR, calls `POST /v1/agents` on the runtime control plane, and transitions the CR through `Syncing → Ready`.

### 5 — Watch reconciliation

```bash
kubectl get vagent -w
# NAME      READY   SYNCED   MANIFESTVALID   AGE
# weather   True    True     True            8s
```

Verify the agent appeared in the registry:

```bash
vais get weather
# NAME     VERSION  HANDLER                        STATUS
# weather  1.0      MyApp.Agents.WeatherAgent      Ready
```

### 6 — Update the spec

Edit `sample-agent.yaml` to bump `version: "2.0"`, then re-apply:

```bash
kubectl apply -f samples/KubernetesOperatorQuickstart/sample-agent.yaml
# agent.vais.io/weather configured
```

The operator detects the spec-hash change, calls `PUT /v1/agents/weather`, and the `Synced` condition re-transitions through `False → True`.

### 7 — Tear down

```bash
kubectl delete -f samples/KubernetesOperatorQuickstart/sample-agent.yaml
# agent.vais.io/weather deleted
# (operator calls DELETE /v1/agents/weather via finalizer)

helm uninstall vais-agents-operator -n vais-agents
helm uninstall vais-agents-runtime  -n vais-agents
```

## What it demonstrates

- **`vais.io/v1alpha1` Agent CRD** — `spec.agentId`, `spec.version`, `spec.handler`, `spec.protocols`, `spec.tools`, `spec.description` map 1:1 to `AgentManifest` fields.
- **Operator reconcile loop** — spec-hash diffing drives idempotent create-or-update; the operator never polls, only watches `Agent` CR events.
- **Three CRD conditions** — `Ready`, `Synced`, `ManifestValid` track the phases of operator→control-plane synchronisation.
- **Finalizer-based delete** — `kubectl delete` triggers the finalizer which calls `DELETE /v1/agents/{id}` before the CR is removed from etcd, preventing stale registry entries.
- **`vais.io/tenant-id` annotation** — forwarded as the `tenantId` claim in every lifecycle call, letting the OPA policy engine scope decisions to a tenant.

## Docs

- [Kubernetes operator concept](../../docs/concepts/kubernetes-operator.md)
- [Deploy the Kubernetes operator](../../docs/guides/deploy-the-kubernetes-operator.md)
- [Deploy the runtime to Kubernetes](../../docs/guides/deploy-the-runtime-to-kubernetes.md)
- [`runtime-docker-compose`](../runtime-docker-compose) — docker-compose path (no K8s required)
