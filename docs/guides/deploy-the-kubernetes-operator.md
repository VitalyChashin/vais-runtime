# Guide: deploy the Kubernetes operator

End-to-end walkthrough from a clean Docker Desktop Kubernetes cluster to a running `Agent` custom resource whose status is tracked by the `vais-agents-operator`. Built on what ships in `deploy/` in v0.13.

Prereqs: Docker Desktop with Kubernetes enabled, Helm 3.12+, kubectl, and a v0.6 HTTP control plane reachable from pods in the cluster (for local dev, use `host.docker.internal:5080` to hit a control-plane process running on your laptop).

## What we'll end up with

```
┌─ vais-agents namespace ──────────────┐    ┌─ default namespace ─────────────┐
│                                      │    │                                 │
│  vais-agents-operator Deployment     │    │  Agent/weather CR (vais.io/v1…) │
│    └── watches `vais.io/v1alpha1`    │◀───┤  (kubectl apply)                │
│    └── reconciles → HTTP calls       │    │                                 │
│                                      │    │  status:                        │
│         projected SA token          ─┼──▶ │    phase: Active                │
│         Authorization: Bearer …      │    │    conditions: [Ready, Synced…] │
│                                      │    │    manifestRevision: sha256:…   │
└──────┬───────────────────────────────┘    └─────────────────────────────────┘
       │ POST /v1/agents/weather
       │ Idempotency-Key: {uid}:{gen}:create
       ▼
  host.docker.internal:5080
  (your v0.6 HTTP control plane)
```

## 1. Build the operator image

The in-repo `Vais.Agents.Control.KubernetesOperator.Host` project packages as a multi-stage Docker build:

```bash
cd G:/work/VAIS2_Platform/Vais2Platform/oss/agentic

docker build \
  -t vais-agents-operator:0.13.0-preview \
  -f src/Vais.Agents.Control.KubernetesOperator.Host/Dockerfile \
  .
```

The build uses `mcr.microsoft.com/dotnet/sdk:9.0` for the build stage and `mcr.microsoft.com/dotnet/aspnet:9.0-alpine` for runtime. The final image runs as non-root (uid/gid 65532) and exposes `/healthz` + `/readyz` on port 8080. Expect ~120 MB.

```bash
docker images | grep vais-agents-operator
# vais-agents-operator  0.13.0-preview  …  118MB
```

Docker Desktop's Kubernetes uses the local Docker daemon as its image registry — no push step needed. For a remote cluster, `docker push` to your registry after tagging.

## 2. Start the v0.6 HTTP control plane

The operator talks to whatever HTTP control-plane host you've already built on top of `Vais.Agents.Control.Http.Server`. For local dev, the simplest thing is a minimal AspNet host:

```csharp
using Vais.Agents.Control.Http;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddAgentControlPlane();
builder.Services.AddAgentControlPlaneIdempotency();            // v0.11 — required for safe reconcile replays

var app = builder.Build();
app.UseAgentControlPlaneIdempotency();
app.MapAgentControlPlane();
app.Run("http://0.0.0.0:5080");
```

Run it:

```bash
dotnet run --urls http://0.0.0.0:5080
```

From inside Docker Desktop's cluster, pods reach the host process at `host.docker.internal:5080`. Confirm from your laptop:

```bash
curl http://localhost:5080/healthz
# 200 OK
```

## 3. Install the operator chart

```bash
helm install vais-agents-operator ./deploy/helm/vais-agents-operator \
  --namespace vais-agents --create-namespace \
  --set controlPlane.baseUrl=http://host.docker.internal:5080 \
  --set image.tag=0.13.0-preview \
  --set image.pullPolicy=IfNotPresent   # we built locally; never pull
```

The install:

- Registers the `Agent` CRD (`agents.vais.io`, `vais.io/v1alpha1`) via a `helm.sh/hook: pre-install` — the CRD lands before the operator pod starts so the watcher doesn't fail to establish.
- Creates the `vais-agents-operator` ServiceAccount in the `vais-agents` namespace.
- Binds a ClusterRole granting get/list/watch/patch on `agents.vais.io` cluster-wide + get on core `secrets` (for `spec.secretRefs` validation).
- Creates a Deployment with 1 replica, projected SA token mounted at `/var/run/secrets/tokens/vais-runtime-token`.

Verify:

```bash
kubectl get pods -n vais-agents
# NAME                                      READY   STATUS    RESTARTS   AGE
# vais-agents-operator-5c9b7f7c4d-xk2pq     1/1     Running   0          18s

kubectl get crd agents.vais.io
# NAME              CREATED AT
# agents.vais.io    …

kubectl logs -n vais-agents -l app.kubernetes.io/name=vais-agents-operator | head
# Vais.Agents operator starting …
# Watching agents.vais.io (cluster-wide)
# Control plane: http://host.docker.internal:5080
```

## 4. Apply an Agent CR

```yaml
# weather-agent.yaml
apiVersion: vais.io/v1alpha1
kind: Agent
metadata:
  name: weather
  namespace: default
  annotations:
    vais.io/tenant-id: tenant-42
spec:
  agentId: weather
  version: "1.0"
  handler:
    typeName: MyApp.WeatherAgent
  protocols:
    - kind: Http
  tools:
    - name: get_weather
  description: Answers questions about the weather.
```

```bash
kubectl apply -f weather-agent.yaml
# agent.vais.io/weather created
```

The operator sees the create event within a couple of seconds, hashes the spec, calls `POST /v1/agents/weather` on the control plane with:

```
Idempotency-Key: a8b9c7de-1234-…:1:create
```

…and writes status. Watch the lifecycle:

```bash
kubectl get vagent -w
# NAME      AGENT ID   VERSION   PHASE      READY   AGE
# weather   weather    1.0       Pending            1s
# weather   weather    1.0       Creating           2s
# weather   weather    1.0       Active     True    3s
```

`vagent` is the short name — the CRD registers both `vagent` and `vagents` aliases for convenience. Describe for the full picture:

```bash
kubectl describe vagent/weather
```

```
Name:         weather
Namespace:    default
API Version:  vais.io/v1alpha1
Kind:         Agent
Spec:
  Agent Id:  weather
  Version:   1.0
  Handler:
    Type Name:  MyApp.WeatherAgent
Status:
  Agent Handle:
    Agent Id:     weather
    Version:      1.0
    Instance Id:  a8b9c7de-…
  Manifest Revision:      sha256:9f2d8c…
  Observed Generation:    1
  Last Reconciled At:     2026-04-20T10:15:00Z
  Phase:                  Active
  Conditions:
    Last Transition Time:  2026-04-20T10:15:00Z
    Message:               Agent registered with control plane.
    Observed Generation:   1
    Reason:                ReconcileSucceeded
    Status:                True
    Type:                  Ready
    …
    Reason:                RuntimeMatchesSpec
    Status:                True
    Type:                  Synced
    …
    Reason:                ValidationPassed
    Status:                True
    Type:                  ManifestValid
```

## 5. Change the spec — watch the update path

Bump the agent's version:

```bash
kubectl patch vagent/weather --type=merge -p '{"spec":{"version":"1.1"}}'
```

```bash
kubectl get vagent -w
# weather   weather   1.1   Updating     1   2s
# weather   weather   1.1   Active       1   3s
```

Operator recomputes `SpecHasher.Compute(spec)`, sees a mismatch vs. `status.manifestRevision`, calls `PUT /v1/agents/weather` with:

```
Idempotency-Key: a8b9c7de-1234-…:2:update
```

Second reconcile on the same generation (e.g. the operator crashes mid-call and KubeOps replays) finds the same key in the v0.11 idempotency store → cached response → `Idempotency-Replayed: true` → no double-update.

## 6. Delete — finalizer path

```bash
kubectl delete vagent/weather
# agent.vais.io "weather" deleted
```

Under the hood: `metadata.deletionTimestamp` is set, Phase flips to `Terminating`, `AgentEntityFinalizer.FinalizeAsync` runs. Default (`preserveOnDelete: false`) calls `DELETE /v1/agents/weather` on the control plane, then removes the `vais.io/agent-deactivate` finalizer — letting K8s garbage-collect the CR.

If you want K8s to release the CR **without** evicting the agent state:

```bash
kubectl patch vagent/weather --type=merge -p '{"spec":{"preserveOnDelete":true}}'
kubectl delete vagent/weather
```

Finalizer runs, skips the eviction call, and the agent stays registered with the control plane. Useful for migrating the declarative source-of-truth between tools without downtime.

## 7. Teardown

```bash
kubectl delete vagent --all --all-namespaces
helm uninstall vais-agents-operator --namespace vais-agents
kubectl delete namespace vais-agents
kubectl delete crd agents.vais.io    # retained by default — clean up explicitly
```

Dropping the CRD after the operator is uninstalled is intentional — removing the CRD while CR instances exist leaves them orphaned (K8s refuses to delete the CRD while instances live). Clear the instances first.

## Common pitfalls

- **Operator restarts in a loop, logs `Unable to retrieve token from …/vais-runtime-token`.** Projected SA token takes a moment on first pod start; the container's first request usually hits before kubelet has materialised the file. `ServiceAccountTokenHandler` retries — wait for the next reconcile tick.
- **`controlPlane.baseUrl` is a localhost URL from the laptop's perspective.** Pods can't reach `localhost`. Use `host.docker.internal` on Docker Desktop, or deploy the control plane into the cluster.
- **`Phase: Error`, `Ready: False`, `Reason: SecretResolutionFailed`.** Your CR references a `secretRef` that doesn't exist in the CR's namespace. Check `Message` for the exact secret + key. Create the Secret or remove the ref; reconcile retries automatically.
- **Status stale after a spec edit.** Compare `metadata.generation` vs `status.observedGeneration` — if the numbers differ, the operator hasn't finished reconciling. `kubectl get vagent -w` surfaces the transition live.
- **CRD installed from a previous version blocks the `helm install --set rbac.installCrd=true`.** Helm refuses to take ownership of an existing CRD. Either delete the CRD first or re-install with `--set rbac.installCrd=false` and apply `deploy/crds/vais.io_agents.yaml` manually.

## See also

- [Kubernetes operator concept](../concepts/kubernetes-operator.md) — reconcile model + condition semantics.
- [Agent CRD reference](../reference/agent-crd.md) — full spec + status schema.
- [Enable HTTP idempotency](enable-http-idempotency.md) — v0.11 idempotency behaviour the operator depends on.
- [Wire a sidecar OPA against the operator](wire-a-sidecar-opa-against-the-operator.md) — combined v0.13 + v0.14 policy deployment.
- `deploy/README.md` + `deploy/helm/vais-agents-operator/README.md` — in-repo deployment notes.
- `samples/KubernetesOperatorWalkthrough` — runnable walkthrough (pending — see [samples plan](../../plans/actor-agents-oss-housekeeping-samples-plan.md)).
