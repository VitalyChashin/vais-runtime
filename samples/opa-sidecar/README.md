# OPA sidecar pattern for the Vais.Agents operator / runtime

`Vais.Agents.Control.Policy.Opa` is a pure-HTTP adapter; it never
distributes or reloads Rego files. This document shows the
ConfigMap-mounted sidecar pattern against the v0.13
`deploy/helm/vais-agents-operator/` chart.

## Topology

```
┌───────────────────── Pod ──────────────────────┐
│                                                │
│  ┌──────────────────┐    loopback HTTP         │
│  │ vais-operator    │ ─────────────►  ┌─────┐  │
│  │ (or runtime)     │                 │ OPA │  │
│  │                  │ ◄─────────────  │ :8181│ │
│  └──────────────────┘                 └─────┘  │
│         ▲                                ▲     │
│         │ ConfigMap mount                │     │
│         │                                │     │
└─────────┼────────────────────────────────┼─────┘
          │                                │
  ┌───────┴───────┐                  ┌─────┴─────┐
  │ ConfigMap     │                  │ ConfigMap │
  │ vais-config   │                  │ vais-rego │
  └───────────────┘                  └───────────┘
```

Operator / runtime talks to OPA over pod-local loopback (~1–5 ms).

## Helm overlay

Save as `values.overlay.yaml` alongside
[`deploy/helm/vais-agents-operator/values.yaml`](../../deploy/helm/vais-agents-operator/values.yaml)
and merge at install time:

```yaml
# values.overlay.yaml
extraVolumes:
  - name: opa-policies
    configMap:
      name: vais-rego

# Add a sidecar container alongside the operator
extraContainers:
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
    readinessProbe:
      httpGet:
        path: /health
        port: 8181
      initialDelaySeconds: 2
      periodSeconds: 5
    resources:
      requests:
        cpu: 20m
        memory: 32Mi
      limits:
        memory: 64Mi

# Have the operator call the loopback sidecar
extraEnv:
  - name: Vais__KubernetesOperator__OpaBaseUrl
    value: http://127.0.0.1:8181
```

The shipped Deployment template in v0.13 does **not** yet expose
`extraContainers` / `extraVolumes` / `extraEnv` — this overlay is a
preview of the integration planned for v0.14.1. For v0.14.0-preview,
hand-patch your Deployment or maintain a downstream chart fork.

## Creating the Rego ConfigMap

Place your policy bundle at `deploy/k8s/opa-policies/`:

```bash
kubectl create configmap vais-rego \
  --from-file=vais-agents.rego=samples/opa-policies/tenant-scoped-allow.rego \
  --namespace vais-agents
```

Or compose multiple samples into one file and mount as a single key.

## Runtime-side wiring

On the runtime side (Host exe using `AddAgentKubernetesOperator` +
`AddOpaPolicyEngine`):

```csharp
builder.Services.AddOpaPolicyEngine(opts =>
{
    opts.BaseUrl = new Uri(builder.Configuration["Vais:OpaBaseUrl"]
                           ?? "http://127.0.0.1:8181");
    opts.DataPath = "vais/agents/allow";
    opts.FailMode = OpaFailMode.Closed;    // production default
    opts.DecisionCacheTtl = TimeSpan.FromSeconds(5);
});
```

`AgentLifecycleManager` consumes `IAgentPolicyEngine` from DI; no
further wiring needed.

## Known limitations (v0.14.0-preview)

- v0.13 operator chart doesn't yet expose the `extraContainers` hook —
  overlay requires a manual patch or chart fork.
- Policy reload on ConfigMap change relies on OPA's `--watch` flag or
  a rolling-restart of the Deployment. For production, consider the
  bundle server pattern (see below).
- Fail-mode semantics are caller-side only. If OPA itself is reachable
  but Rego has a bug (e.g. missing `allow` rule), you get a 4xx from
  the adapter (`InvalidOperationException`) — diagnostic error, not a
  policy denial. Fix the rego and restart.

---

## Bundle server pattern (v0.32+)

The **runtime Helm chart** (`deploy/helm/vais-agents-runtime`) now natively
supports OPA bundle-server polling + RS256/ES256/HS256 bundle signature
verification via the `opa.bundle.*` values block — no manual chart patching
required.

See [`samples/opa-bundle-server/README.md`](../opa-bundle-server/README.md)
for the full workflow: build a bundle → sign with `opa sign` → serve via
nginx → configure the Helm chart → OPA polls + verifies.
