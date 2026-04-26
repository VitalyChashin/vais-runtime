# Guide: gate agents with OPA

End-to-end walkthrough: run an OPA server locally, write a tenant-scoped Rego policy, register `AddOpaPolicyEngine`, observe denials in the audit log. No Kubernetes involved — just a Docker container for OPA and a .NET host for the control plane.

For the K8s sidecar deployment pattern see [wire a sidecar OPA against the operator](wire-a-sidecar-opa-against-the-operator.md).

## Packages

```xml
<PackageReference Include="Vais.Agents.Control.Http.Server" Version="0.15.0-preview" />
<PackageReference Include="Vais.Agents.Control.Policy.Opa" Version="0.15.0-preview" />
```

## 1. Run OPA locally

Pull + run the official container, exposing the HTTP API on port 8181:

```bash
docker run -d --name opa \
  -p 8181:8181 \
  openpolicyagent/opa:1.15.2 \
  run --server --addr :8181
```

Verify:

```bash
curl http://localhost:8181/health
# {}
```

OPA is running without any policies loaded. Next we push one.

## 2. Write a tenant-scoped policy

Save as `tenant-scoped-allow.rego`:

```rego
package vais.agents

default allow := {"allowed": true}

# Invoke / Signal / Query must match tenant.
allow := {"allowed": true} if {
    gated_operation
    input.principal != null
    input.agent != null
    input.agent.labels.tenant == input.principal.tenantId
}

allow := {"allowed": false, "reason": "cross-tenant access denied"} if {
    gated_operation
    input.principal != null
    input.agent != null
    input.agent.labels.tenant != input.principal.tenantId
}

allow := {"allowed": false, "reason": "unauthenticated caller"} if {
    gated_operation
    input.principal == null
}

gated_operation if { input.operation == "Invoke" }
gated_operation if { input.operation == "Signal" }
gated_operation if { input.operation == "Query" }
```

The policy gates three verbs (`Invoke`, `Signal`, `Query`) on tenant match. `Create` / `Update` / `Cancel` / `Evict` fall through the `default` rule → allow. `package vais.agents` + the `allow` rule name match the adapter's default `DataPath = "vais/agents/allow"`.

## 3. Push the policy to OPA

OPA's policy API accepts Rego via `PUT`:

```bash
curl -X PUT --data-binary @tenant-scoped-allow.rego \
     http://localhost:8181/v1/policies/vais-agents
# {}
```

Verify:

```bash
curl http://localhost:8181/v1/policies/vais-agents | jq '.result.id'
# "vais-agents"
```

Alternatively, start OPA with the policy mounted:

```bash
docker run -d --name opa \
  -p 8181:8181 \
  -v $PWD:/policies \
  openpolicyagent/opa:1.15.2 \
  run --server --addr :8181 /policies
```

## 4. Sanity-check the decision

Before wiring any C#, confirm OPA gives the answers you expect:

```bash
# Same-tenant invoke → allow
curl -X POST --data '{"input":{
  "schemaVersion": "1",
  "operation": "Invoke",
  "principal": {"id": "alice", "tenantId": "tenant-a"},
  "agent": {"id": "weather", "labels": {"tenant": "tenant-a"}}
}}' \
http://localhost:8181/v1/data/vais/agents/allow
# {"result": {"allowed": true}}

# Cross-tenant invoke → deny
curl -X POST --data '{"input":{
  "schemaVersion": "1",
  "operation": "Invoke",
  "principal": {"id": "bob", "tenantId": "tenant-b"},
  "agent": {"id": "weather", "labels": {"tenant": "tenant-a"}}
}}' \
http://localhost:8181/v1/data/vais/agents/allow
# {"result": {"allowed": false, "reason": "cross-tenant access denied"}}

# Unauthenticated → deny
curl -X POST --data '{"input":{
  "schemaVersion": "1",
  "operation": "Invoke",
  "principal": null,
  "agent": {"id": "weather", "labels": {"tenant": "tenant-a"}}
}}' \
http://localhost:8181/v1/data/vais/agents/allow
# {"result": {"allowed": false, "reason": "unauthenticated caller"}}
```

Green — the policy is doing what the Rego says.

## 5. Wire the adapter on the control plane

Typical control-plane host:

```csharp
using Vais.Agents.Control.Http;
using Vais.Agents.Control.Policy.Opa;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddAgentControlPlane();
builder.Services.AddAgentControlPlaneIdempotency();

builder.Services.AddOpaPolicyEngine(opts =>
{
    opts.BaseUrl = new Uri("http://localhost:8181");
    opts.DataPath = "vais/agents/allow";
    opts.FailMode = OpaFailMode.Closed;                   // production default — deny on OPA unreachable
    opts.Timeout = TimeSpan.FromMilliseconds(500);
    opts.DecisionCacheTtl = TimeSpan.FromSeconds(5);
});

var app = builder.Build();
app.UseAuthentication();                                  // populate AgentPrincipal from JWT (your wiring)
app.UseAgentControlPlaneIdempotency();
app.MapAgentControlPlane();
app.Run("http://0.0.0.0:5080");
```

`AddOpaPolicyEngine` registers `IAgentPolicyEngine` as a singleton using `TryAddSingleton` — the built-in `AgentLifecycleManager` picks it up automatically and routes every verb through `EvaluateAsync` before touching the agent.

## 6. Observe a denial

Register two agents and invoke them cross-tenant:

```bash
# Register weather agent under tenant-a
curl -X POST http://localhost:5080/v1/agents \
  -H "Authorization: Bearer $(tenant_a_jwt)" \
  -H "Content-Type: application/json" \
  -H "Idempotency-Key: reg-weather-1" \
  -d '{
    "id": "weather",
    "version": "1.0",
    "handler": { "typeName": "MyApp.WeatherAgent" },
    "protocols": [{"kind": "Http"}],
    "tools": [],
    "labels": { "tenant": "tenant-a" }
  }'
# 201 Created

# Same-tenant invoke → 200 OK
curl -X POST http://localhost:5080/v1/agents/weather/invoke \
  -H "Authorization: Bearer $(tenant_a_jwt)" \
  -H "Content-Type: application/json" \
  -H "Idempotency-Key: inv-same-1" \
  -d '{"text":"What is the weather in Tokyo?"}'
# 200 OK { "output": "…" }

# Cross-tenant invoke → 403 + URN
curl -X POST http://localhost:5080/v1/agents/weather/invoke \
  -H "Authorization: Bearer $(tenant_b_jwt)" \
  -H "Content-Type: application/json" \
  -H "Idempotency-Key: inv-cross-1" \
  -d '{"text":"What is the weather in Tokyo?"}'
# HTTP/1.1 403 Forbidden
# Content-Type: application/problem+json
#
# {
#   "type":   "urn:vais-agents:policy-denied",
#   "title":  "Policy denied",
#   "detail": "cross-tenant access denied",
#   "status": 403
# }
```

The `detail` field carries the `reason` string from the Rego `{"allowed": false, "reason": "..."}` object — flows through `PolicyDecision.Deny(reason)` → `ProblemDetails.Detail`. See [problem-details URNs](../reference/problem-details-urns.md) for the full failure-shape table.

## 7. Observe the audit trail

The adapter emits a `Vais.Agents.Policy.OPA` activity per evaluation. Wire OTel to see decisions in your traces:

```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(t => t
        .AddAgenticInstrumentation()
        .AddConsoleExporter());
```

Console output for the denied invoke:

```
Activity.Name:        Vais.Agents.Policy.OPA
Activity.Kind:        Client
Activity.DisplayName: policy.evaluate Invoke
Activity.StatusCode:  Error
Activity.StatusDescription: cross-tenant access denied
Activity.Tags:
    vais.policy.operation:          Invoke
    vais.policy.agent.id:           weather
    vais.policy.agent.version:      1.0
    vais.policy.principal.tenant:   tenant-b
    vais.policy.cache-hit:          False
    vais.policy.decision:           deny
    vais.policy.deny-reason:        cross-tenant access denied
    vais.policy.opa.status-code:    200
```

A deny is a span-status `Error` with the reason as description — standard OTel conventions. Dashboards filtering on `StatusCode=Error` + `vais.policy.decision=deny` surface deny volume + reasons at a glance.

## 8. Flip `FailMode` for dev ergonomics

During local development you may not want the control plane to hard-fail when you shut OPA down:

```csharp
builder.Services.AddOpaPolicyEngine(opts =>
{
    opts.FailMode = builder.Environment.IsDevelopment()
        ? OpaFailMode.Open       // dev: proceed if OPA is unreachable
        : OpaFailMode.Closed;    // prod: deny if OPA is unreachable
});
```

`FailMode.Open` is **dev only**. Production must always pin `Closed` — the entire point of gating is that "OPA unreachable" fails the same way as "OPA said no". See [`OPA policy engine` concept](../concepts/opa-policy-engine.md) for the full FailMode semantics.

## 9. Diagnose 4xx — the "adapter bug" path

Intentionally break the `DataPath`:

```csharp
opts.DataPath = "vais/typo/allow";   // doesn't exist in OPA's policy namespace
```

Invoke:

```
HTTP/1.1 500 Internal Server Error

InvalidOperationException: OPA returned 404 —
likely wrong DataPath ('vais/typo/allow') or malformed request.
Response body: {"code":"undefined_document","message":"..."}
```

The adapter does **not** apply `FailMode` on 4xx — 4xx is treated as an adapter / config bug that deserves operator attention. Fix the option; don't paper over with `FailMode.Open`. See the "4xx is a bug, 5xx is a policy path" rule in the concept page.

## 10. Teardown

```bash
docker rm -f opa
```

## Known pitfalls

- **`Authentication` middleware must run before `IAgentPolicyEngine`** — without it, `AgentPrincipal` is null and your Rego's `input.principal != null` branches don't take. Either populate `principal` in a custom `IAgentPrincipalProvider` or run behind `UseAuthentication()` + JWT bearer.
- **`Idempotency-Replayed` responses bypass the policy engine.** First request goes through OPA; replays served from the v0.11 idempotency cache don't re-evaluate. If a policy gets stricter between the original call and the replay, the replay still returns the cached original response. Keep `DecisionCacheTtl` ≤ `IdempotencyOptions.Ttl` to minimize window mismatch.
- **OPA's `--watch` flag hot-reloads policies from disk** but not from the HTTP API. If you pushed the policy via `PUT /v1/policies/...`, restart OPA to pick up file changes.
- **`decisionCache.TtlSeconds: 0` disables the cache** — useful for audit / debugging environments where every call must hit OPA. 1–5 ms per call instead of sub-microsecond cache hit. Tune per environment.

## Production: signed bundle server (v0.32)

The sidecar-with-ConfigMap pattern used in this guide is the local/cluster-dev pattern. For production — where you want the policy lifecycle decoupled from the operator lifecycle, RS256-signed bundles, and GitOps-style rollouts — switch to bundle-server mode:

1. Run a signed nginx bundle server (see `samples/opa-bundle-server/` — includes `sign-bundle.sh`, `docker-compose.yaml`, and a full `README.md`).
2. Enable bundle mode in Helm: set `opa.bundle.enabled: true` + `opa.bundle.url` + `opa.bundle.signing.*` in `values.yaml`.
3. OPA switches to `--config-file` startup and polls the bundle URL on the configured interval. Policy updates deploy without touching the operator or runtime.

See [OPA policy engine — Bundle-server mode](../concepts/opa-policy-engine.md#bundle-server-mode-v032) for the full Helm values reference.

## See also

- [OPA policy engine concept](../concepts/opa-policy-engine.md) — adapter internals, wire contract, FailMode semantics.
- [Author a Rego policy against the VAIS input schema](author-a-rego-policy-against-the-vais-input-schema.md) — the four guard patterns.
- [Wire a sidecar OPA against the operator](wire-a-sidecar-opa-against-the-operator.md) — K8s sidecar deployment.
- `contracts/opa-input-schema.md` — the v1 input shape Rego reads.
- `samples/opa-policies/tenant-scoped-allow.rego` — the source of this guide's policy.
- `samples/opa-bundle-server/` — nginx bundle server + signing scripts (v0.32).
- [Problem-details URNs](../reference/problem-details-urns.md) — the `urn:vais-agents:policy-denied` URN.
