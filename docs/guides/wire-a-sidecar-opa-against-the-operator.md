# Guide: wire a sidecar OPA against the operator

Combine v0.13 (Kubernetes operator) + v0.14 (OPA policy engine). Deploy an OPA sidecar alongside the operator / runtime pod, mount Rego policies via a ConfigMap, and flip `Vais.Agents.Control.Policy.Opa` to query the sidecar at `http://127.0.0.1:8181/v1/data/vais/agents/allow`. Every `IAgentLifecycleManager.InvokeAsync` / `CreateAsync` / `SignalAsync` / … call runs through OPA before the runtime touches the agent.

Tight admission-control loop (~1–5 ms round-trip over pod loopback) with the policy engine decoupled from the runtime — operators ship Rego without redeploying the operator binary.

Prereqs: the [deploy-the-kubernetes-operator](deploy-the-kubernetes-operator.md) guide is completed — you have an operator running in the `vais-agents` namespace. You also need an understanding of OPA fundamentals — the [OPA documentation](https://www.openpolicyagent.org/docs/) is the authoritative reference.

## Topology

```
┌────────────── vais-agents-operator pod ───────────────┐
│                                                       │
│  ┌──────────────────┐    http://127.0.0.1:8181        │
│  │ operator         │ ─────────────────────────────▶  │
│  │ container        │                                 │
│  │                  │ ◀────── {"result":{"allowed":…} │
│  └──────────────────┘                                 │
│         ▲                              ▲              │
│         │ SA token                     │              │
│         ▼                       ┌──────┴──────┐       │
│                                 │ opa sidecar │       │
│  http://control-plane:5080      │  :8181      │       │
│  (outbound, w/ Authorization)   └──────┬──────┘       │
│                                        │              │
│                                  /policies (RO mount) │
│                                        ▲              │
└────────────────────────────────────────┼──────────────┘
                                         │
                                ┌────────┴────────┐
                                │ ConfigMap       │
                                │ vais-rego       │
                                │  (tenant-scope, │
                                │   model-allow,  │
                                │   budget-cap)   │
                                └─────────────────┘
```

The operator + OPA share a pod — loopback means OPA latency sits well under the 50 ms SLO for reconcile-tick policy checks.

## 1. Author the Rego policy

Start from one of the shipped samples. `samples/opa-policies/tenant-scoped-allow.rego` denies cross-tenant invocations:

```rego
package vais.agents

import future.keywords.if
import future.keywords.in

default allow := {"allowed": false, "reason": "Policy denied — no matching rule."}

allow := {"allowed": true} if {
    input.schemaVersion == "1"
    input.operation in ["Invoke", "Signal", "Query"]
    input.principal.tenantId == input.agent.labels.tenantId
}

allow := {"allowed": true} if {
    input.schemaVersion == "1"
    input.operation == "Create"
    input.principal.scopes[_] == "vais.agents:platform-admin"
}
```

The shipped `OpaPolicyEngine` expects exactly two response shapes — either `allow = true/false` (bool) or `allow = {"allowed": bool, "reason": string?}` (object). The object shape carries a human-readable reason to the audit log on deny; samples all use it.

Compose multiple policies by either:

- Concatenating into one `.rego` file with a single `allow` rule that `or`s the checks.
- Splitting into separate packages and importing — see `samples/opa-policies/README.md` for the pattern.

## 2. Package the policy as a ConfigMap

```bash
kubectl create configmap vais-rego \
  --from-file=vais-agents.rego=samples/opa-policies/tenant-scoped-allow.rego \
  --namespace vais-agents
```

Verify:

```bash
kubectl get configmap vais-rego -n vais-agents -o yaml | head
# apiVersion: v1
# data:
#   vais-agents.rego: |
#     package vais.agents
#     …
```

Updating the policy = updating the ConfigMap:

```bash
kubectl create configmap vais-rego \
  --from-file=vais-agents.rego=./new-policy.rego \
  --namespace vais-agents \
  --dry-run=client -o yaml | kubectl apply -f -
```

## 3. Overlay the sidecar onto the operator pod

The v0.13 chart doesn't yet expose `extraContainers` / `extraVolumes` hooks (landing in v0.14.1 polish). For v0.14.0-preview, either:

### Option A — `kubectl patch` the Deployment (quickest)

```bash
kubectl patch deployment vais-agents-operator -n vais-agents --type=strategic -p "$(cat <<'EOF'
spec:
  template:
    spec:
      volumes:
        - name: opa-policies
          configMap:
            name: vais-rego
      containers:
        - name: opa
          image: openpolicyagent/opa:1.15.2
          args:
            - run
            - --server
            - --addr
            - "127.0.0.1:8181"
            - /policies
          ports:
            - containerPort: 8181
              name: opa
          volumeMounts:
            - name: opa-policies
              mountPath: /policies
              readOnly: true
          readinessProbe:
            httpGet:
              path: /health
              port: 8181
            initialDelaySeconds: 2
            periodSeconds: 5
          resources:
            requests: { cpu: 20m, memory: 32Mi }
            limits:   { memory: 64Mi }
        - name: vais-agents-operator
          env:
            - name: Vais__KubernetesOperator__OpaBaseUrl
              value: http://127.0.0.1:8181
EOF
)"
```

The patch adds the `opa` sidecar container, the `opa-policies` volume mount, and an env var the operator reads on startup to discover the sidecar.

### Option B — fork the Helm chart

Copy `deploy/helm/vais-agents-operator/` into your own repo, add the `extraContainers` / `extraVolumes` hooks to `templates/deployment.yaml`, and maintain the fork until v0.14.1 lands the hooks upstream. See `samples/opa-sidecar/README.md` for the overlay skeleton.

## 4. Verify the sidecar

```bash
kubectl rollout status deploy/vais-agents-operator -n vais-agents
# deployment "vais-agents-operator" successfully rolled out

kubectl get pods -n vais-agents
# NAME                                      READY   STATUS    RESTARTS   AGE
# vais-agents-operator-7f9d8c6b4d-m2p8q     2/2     Running   0          30s   ← 2/2 now
```

Check OPA's liveness + loaded policy:

```bash
kubectl exec -n vais-agents deploy/vais-agents-operator -c opa -- \
    wget -qO- http://127.0.0.1:8181/health
# {}

kubectl exec -n vais-agents deploy/vais-agents-operator -c opa -- \
    wget -qO- http://127.0.0.1:8181/v1/policies | head
# {"result": [{"id":"/policies/vais-agents.rego",…}]}
```

## 5. Wire `AddOpaPolicyEngine` on the control-plane side

The policy engine runs on the **control plane** (not the operator) — that's where `IAgentLifecycleManager` lives and where every verb flows through. Add to the host that serves the HTTP control plane:

```csharp
using Vais.Agents.Control.Policy.Opa;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddAgentControlPlane();
builder.Services.AddAgentControlPlaneIdempotency();

builder.Services.AddOpaPolicyEngine(opts =>
{
    opts.BaseUrl = new Uri(builder.Configuration["Vais:OpaBaseUrl"]
                           ?? "http://127.0.0.1:8181");
    opts.DataPath = "vais/agents/allow";
    opts.FailMode = OpaFailMode.Closed;            // production default — deny on runtime error
    opts.DecisionCacheTtl = TimeSpan.FromSeconds(5);
    opts.TimeoutMs = 250;
});

var app = builder.Build();
app.UseAgentControlPlaneIdempotency();
app.MapAgentControlPlane();
app.Run();
```

`AgentLifecycleManager` picks up `IAgentPolicyEngine` from DI — no further wiring. Every verb flows through:

```
IAgentLifecycleManager.CreateAsync
  → OpaPolicyEngine.IsAllowedAsync
      → POST http://127.0.0.1:8181/v1/data/vais/agents/allow
        { "input": { "schemaVersion": "1", "operation": "Create", "principal": {…}, "agent": {…} } }
      ← { "result": {"allowed": true} }
  → proceed
```

If the same control-plane host runs in the cluster (same pod as the OPA sidecar), `BaseUrl: http://127.0.0.1:8181` is correct. If the control plane lives in a separate Deployment + wants its own OPA sidecar, each host wires its own loopback OPA.

## 6. Trigger a policy decision

Apply an Agent CR whose `tenant-id` annotation **mismatches** the principal your test caller carries:

```bash
kubectl apply -f - <<'EOF'
apiVersion: vais.io/v1alpha1
kind: Agent
metadata:
  name: cross-tenant-attempt
  namespace: default
  annotations:
    vais.io/tenant-id: tenant-other
spec:
  agentId: cross-tenant
  version: "1.0"
  handler: { typeName: MyApp.WeatherAgent }
  protocols: []
  tools: []
  labels:
    tenantId: tenant-other
EOF
```

From the operator's perspective the `CreateAsync` call succeeds (the create verb bypasses OPA unless your Rego gates it; the shipped `tenant-scoped-allow.rego` only gates Invoke/Signal/Query). Subsequent invoke attempts fail:

```bash
# From a client in a different tenant context:
curl -X POST http://control-plane:5080/v1/agents/cross-tenant/invoke \
     -H "Authorization: Bearer <tenant-a-jwt>" \
     -H "Content-Type: application/json" \
     -d '{"text": "hi"}'
# HTTP/1.1 403 Forbidden
# Content-Type: application/problem+json
# {
#   "type": "urn:vais-agents:policy-denied",
#   "title": "Policy denied",
#   "detail": "Policy denied — no matching rule.",
#   "status": 403
# }
```

`status.conditions` on the operator-managed CR doesn't flip — the policy deny happens at invoke time, not at reconcile time. Check the audit log on the control plane to trace:

```
[WARN] OpaPolicyEngine deny: op=Invoke agent=cross-tenant principal=tenant-a-user
       reason="Policy denied — no matching rule."
```

## 7. Hot-reload the policy

Swap the Rego bundle by re-creating the ConfigMap + restarting OPA (or the whole pod):

```bash
kubectl create configmap vais-rego \
  --from-file=vais-agents.rego=./updated-policy.rego \
  --namespace vais-agents \
  --dry-run=client -o yaml | kubectl apply -f -

kubectl rollout restart deploy/vais-agents-operator -n vais-agents
```

Rollout picks up the ConfigMap change in the new pod's `/policies` mount. Next reconcile tick = new policy in effect.

Alternatives — running OPA with `--watch /policies` makes it hot-reload on file change without a container restart; or flipping to a remote OPA bundle server (`opa run --set services.vais.url=https://bundles.internal --set bundles.vais.resource=vais-agents.tar.gz`) decouples the policy lifecycle from the operator lifecycle. Bundle server is the production-grade pattern; sidecar-with-ConfigMap is the cluster-local dev pattern documented here.

## Operational notes

- **Fail-mode.** `FailMode: Closed` denies when OPA is unreachable. Startup ordering matters — if the operator pod restarts and hits OPA before `/policies` is ready, every call denies. OPA's `readinessProbe` mitigates: kubelet withholds traffic to the sidecar until `/health` returns 200. With the SA-token-on-first-start retry already in the operator, ordering is self-healing.
- **Decision cache.** `DecisionCacheTtl: 5s` (SHA-256-keyed per input) dedup's identical verb-principal-agent triples within a 5-second window. 1024 entries max; 25% eldest are purged when full. Sensible default for the reconcile loop's per-tick cadence.
- **4xx responses from OPA are adapter bugs, not policy denials.** `{"result":{}}` (missing `allow`), package path typos, Rego parse errors all surface as `InvalidOperationException` — caller sees a `500`. Fix the policy and restart.
- **Bytes on the wire.** Every policy check ships the full `AgentManifest` as `input.agent` — that's the spec the `OpaInputBuilder` freezes at v1. Large manifests (thousands of tool refs) amplify the OPA sidecar's CPU cost. Filter in Rego, not on the caller side, so the wire shape stays stable.

## See also

- [Kubernetes operator concept](../concepts/kubernetes-operator.md) — the reconcile loop the sidecar gates.
- [Deploy the Kubernetes operator](deploy-the-kubernetes-operator.md) — prerequisite.
- [OPA policy engine concept](../concepts/opa-policy-engine.md) — adapter internals, FailMode semantics (pending — arrives with PR 5).
- `contracts/opa-input-schema.md` — the v1 input schema your Rego reads.
- `samples/opa-sidecar/README.md` — the source overlay this guide adapts.
- `samples/opa-policies/` — three ready-to-use Rego starters.
