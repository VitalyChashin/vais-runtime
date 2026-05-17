# OPA policy engine

Shipped in v0.14 as `Vais.Agents.Control.Policy.Opa` — a pure-HTTP adapter that wraps the `IAgentPolicyEngine` contract (v0.6) around an OPA sidecar. Every verb on the `IAgentLifecycleManager` flows through `EvaluateAsync` → HTTP `POST /v1/data/{DataPath}` → OPA decision → Allow or Deny. Policies live as Rego files in a ConfigMap (or bundle server), decoupled from the runtime binary.

## Why OPA over a custom engine

A handful of runtimes (Dapr Agents, AWS Bedrock AgentCore, Temporal) gate invocations with custom Go / Java policy DSLs. We looked, surveyed, and picked [Open Policy Agent](https://www.openpolicyagent.org) instead:

1. **Rego is the lingua franca.** Every large K8s-native org we talked to already runs OPA for admission control, service-mesh auth, cloud-IAM tooling, or CI-approval gates. Shipping our own DSL would force re-learning + re-tooling for no marginal gain.
2. **Policy is a deployable, not a binary.** OPA runs as a sidecar or standalone; Rego bundles ship through ConfigMaps, Git-ops, or OPA's bundle server. The runtime never has to restart to flip a policy.
3. **Existing tooling.** `opa eval`, `opa test`, `conftest`, VS Code language server, linting (`regal`). Custom engine = new test framework, new editor support, new CI pipeline.
4. **Adapter, not adopter.** `Vais.Agents.Control.Policy.Opa` is 400 lines. Swapping OPA for Cedar or AuthZEN later is a parallel adapter — `IAgentPolicyEngine` stays put.

Trade-off: Rego is a pull — consumers unfamiliar with it hit a learning curve. Mitigated by three shipped starter policies (`samples/opa-policies/`) + the input-schema contract (`contracts/opa-input-schema.md`).

## Wire contract

One HTTP call per `IAgentPolicyEngine.EvaluateAsync` invocation:

```
POST {BaseUrl}/v1/data/{DataPath}

{ "input": {
    "schemaVersion": "1",
    "operation":     "Create" | "Invoke" | "Signal" | "Query" | "Cancel" | "Update" | "Evict",
    "principal":     { "id", "tenantId", "scopes"? } | null,
    "agent":         <full AgentManifest in camelCase> | null
} }
```

`DataPath` defaults to `"vais/agents/allow"`. The adapter trims leading `/` so `DataPath: "/vais/agents/allow"` and `"vais/agents/allow"` produce the same URL.

OPA accepts two response shapes:

```json
{ "result": true }                          ← allow (bool shape, terse)
{ "result": false }                         ← deny, reason = "Policy denied"
{ "result": { "allowed": true } }           ← allow (object shape, structured)
{ "result": { "allowed": false,
              "reason": "cross-tenant" } }  ← deny with audit-friendly reason
```

`OpaResponseParser.Parse(body)` accepts both. The boolean shape is terse + cheap; the object shape carries a `reason` that surfaces to the audit log. The samples use the object shape.

Anything else — missing `result`, non-bool-non-object `result` value, malformed JSON — returns `null` from the parser. The engine then applies `FailMode` (described below).

## The v1 input schema

Stable, versioned, contract-frozen. `OpaInputBuilder.SchemaVersion = "1"`. Every `input.*` field is documented in `contracts/opa-input-schema.md` — see that file for the authoritative field-by-field reference; this section gives the overview.

| Field | Shape | When null |
|---|---|---|
| `schemaVersion` | `"1"` | never |
| `operation` | one of seven `PolicyOperation` enum values | never |
| `principal` | `{ id, tenantId, scopes? }` | anonymous caller (no `AgentPrincipal` resolved upstream) |
| `agent` | full `AgentManifest` round-tripped to camelCase JSON | `Query` against an agent id not in the registry (there's no manifest to send) |

Seven operations gated — `Create`, `Invoke`, `Signal`, `Query`, `Cancel`, `Update`, `Evict`. Every verb on `IAgentLifecycleManager` flows through. Rego policies branch on `input.operation` to gate different verbs differently — the shipped `tenant-scoped-allow.rego` gates Invoke/Signal/Query but leaves Create/Update/Evict allow-all so platform teams keep full control of manifest rollout.

### Schema evolution

Additive fields on the existing v1 shape don't bump the version — Rego policies that don't read the new field keep passing. Breaking changes (renaming a required field, changing the shape of `principal`) bump to `schemaVersion: "2"`. For one minor version, the adapter dual-ships — callers opt in via options. Adding a new operation to the enum does **not** bump the version (Rego can always match `input.operation == "NewThing"` against any enum value). See `contracts/opa-input-schema.md` for the full evolution protocol.

## `FailMode` semantics

Two-value enum. Default `Closed`.

| Mode | OPA unreachable / timeout / malformed response | Posture |
|---|---|---|
| `OpaFailMode.Closed` (default) | `PolicyDecision.Deny(reason)` — request is denied; caller sees `403 urn:vais-agents:policy-denied`. | Enterprise-safe default; no call succeeds when the gate is broken. |
| `OpaFailMode.Open` | `PolicyDecision.Allow` — request proceeds as if no policy was configured. | Dev convenience only; never ship to prod. |

Deploy-time decision. Production configs pin `FailMode: Closed` — the whole point of shipping the engine is that "couldn't reach OPA" fails exactly like "OPA said no." Dev configs flip to `Open` to decouple agent debugging from policy-stack debugging.

## 4xx is a bug, 5xx is a policy path

Non-obvious error-classification rule that drives how the adapter handles OPA HTTP responses:

| OPA response | Adapter behaviour |
|---|---|
| `200 OK` + parseable boolean/object `result` | Parsed decision returned — `Allow` or `Deny`. |
| `200 OK` + unparseable body (missing `result`, wrong kind, bad JSON) | Null from parser → **`FailMode`** applied. Missing `allow` rule, typo in the Rego package path, malformed Rego default all land here. |
| `4xx` (any) | `InvalidOperationException` thrown. Caller sees a `500` from the control plane. |
| `5xx`, `HttpRequestException`, timeout | **`FailMode`** applied. Network unreachable, OPA crashed, DNS failure. |

The rule: **4xx = adapter / config bug** (wrong `DataPath`, missing body, malformed request). Retrying won't help; failing the request loudly gets the operator's attention. **5xx + transport failure = operational issue on OPA's side** — the whole point of `FailMode.Closed` is to keep the system safe during these. **200-with-bad-body = Rego bug** (missing `allow`, mistyped `default`) — same deploy-time fix-it-forward posture as a 4xx, but the adapter treats it as a policy-path failure because the 200 indicates OPA itself is healthy.

If you see `InvalidOperationException: OPA returned 404 — likely wrong DataPath` in a staging log, fix the `DataPath` option — don't paper over with `FailMode.Open`.

## Decision cache

SHA-256-keyed in-memory cache over the serialised `input` JSON. Default TTL 5 seconds; max 1024 entries; 25% eldest purged on overflow.

```csharp
builder.Services.AddOpaPolicyEngine(opts =>
{
    opts.DecisionCacheTtl = TimeSpan.FromSeconds(5);     // default
    opts.DecisionCacheMaxEntries = 1024;                 // default
    // opts.DecisionCacheTtl = TimeSpan.Zero;            // disable caching entirely
});
```

Five seconds is a compromise — tight enough that a policy update via `kubectl rollout restart` propagates on the next reconcile tick, loose enough to absorb the reconcile-loop's per-tick cadence against the same agent. Cache key is SHA-256 of the exact input JSON — any change to `operation`, `principal`, or `agent` produces a fresh key.

Purge semantics: when adding entry #1025, snapshot current entries → sort by insertion timestamp → drop the oldest 256. Small CPU hit at the boundary; prevents unbounded growth without per-entry eviction overhead.

Set `DecisionCacheTtl: TimeSpan.Zero` to disable caching entirely — useful for audit environments where every decision must hit OPA, at a 1-5 ms per-call latency cost.

## Registration + lifetime

```csharp
using Vais.Agents.Control.Policy.Opa;

builder.Services.AddOpaPolicyEngine(opts =>
{
    opts.BaseUrl = new Uri("http://127.0.0.1:8181");     // loopback sidecar default
    opts.DataPath = "vais/agents/allow";
    opts.FailMode = OpaFailMode.Closed;
    opts.Timeout = TimeSpan.FromMilliseconds(500);
});
```

`AddOpaPolicyEngine` registers:

- `OpaPolicyEngineOptions` via the standard options pattern.
- A typed `HttpClient<OpaPolicyEngine>` with `BaseAddress` = `options.BaseUrl` and `Timeout` = `options.Timeout + 1s` (outer bound; inner per-call timeout enforced via `CancellationTokenSource`).
- `IAgentPolicyEngine` → **singleton** `OpaPolicyEngine`. Wrapped via `TryAddSingleton<IAgentPolicyEngine>(sp => sp.GetRequiredService<OpaPolicyEngine>())` — consumers who registered a different `IAgentPolicyEngine` beforehand keep their choice.
- `TimeProvider.System` (if not already registered).

Singleton is deliberate: the decision cache is per-engine, so a new engine instance per request would defeat the cache. `HttpClient` is typed-client, so `IHttpClientFactory` manages connection pooling.

## Observability

`OpaPolicyEngine` emits a standard `Vais.Agents` activity per `EvaluateAsync` call — `Vais.Agents.Policy.OPA` span with tags:

- `vais.policy.operation` — the `PolicyOperation` value.
- `vais.policy.agent.id` / `vais.policy.agent.version` — when the manifest is non-null.
- `vais.policy.principal.tenant` — when the principal is non-null.
- `vais.policy.cache-hit` — `true` / `false`.
- `vais.policy.decision` — `allow` / `deny`.
- `vais.policy.deny-reason` — populated on deny.
- `vais.policy.opa.status-code` — on `200` + `5xx`; absent on transport failures.

These flow through the existing Langfuse enricher (`Vais.Agents.Observability.Langfuse`) and any OTel collector wired via `AddAgenticInstrumentation`. A deny is a span-status `Error` with the deny-reason as the description.

## Bundle-server mode (v0.32)

The Helm chart's `opa.bundle.*` sub-values block switches OPA from sidecar-with-ConfigMap to production bundle-server mode. The `configmap-opa-config.yaml` template renders OPA's native `config.yaml` with env-substitution placeholders so secrets are never baked into the ConfigMap:

```yaml
# values.yaml (relevant excerpt)
opa:
  enabled: true
  bundle:
    enabled: true
    url: "https://bundles.internal"           # bundle server base URL (no path)
    resource: /vais-agents.tar.gz             # path on the bundle server to the archive
    polling:
      minDelaySeconds: 60
      maxDelaySeconds: 120
    # Optional bearer-token auth for the bundle server. Injected as BUNDLE_SERVER_TOKEN.
    serviceAuthTokenSecret: opa-bundle-auth-token   # existing K8s Secret name
    serviceAuthTokenSecretKey: token                # key inside that Secret
    signing:
      enabled: true
      algorithm: RS256                              # RS256 | ES256 | HS256
      keyId: vais-bundle-key                        # must match `opa sign --signing-key-id`
      existingSecret: opa-bundle-signing-key        # existing K8s Secret name
      existingSecretKey: key.pem                    # key inside that Secret
```

When `opa.bundle.enabled: true`:
- The OPA sidecar is started with `--config-file /etc/opa/config.yaml` instead of individual `--policy` flags.
- `configmap-opa-config.yaml` injects `BUNDLE_SERVER_TOKEN` and `OPA_BUNDLE_SIGNING_KEY` from the referenced K8s Secrets as environment variables; OPA's native `${ENV_VAR}` substitution resolves them at runtime — the values never appear in any ConfigMap.
- Signature verification is gated on `opa.bundle.signing.enabled: true`; without it OPA loads the bundle but does not verify the signature.
- Policy hot-reloads flow through OPA's bundle polling interval without a runtime restart.

For a working end-to-end example (nginx bundle server + `sign-bundle.sh` + `docker-compose.yaml`), see `samples/opa-bundle-server/`.

## When OPA is the right choice

- You run K8s and OPA is already deployed for other admission tasks → the sidecar is free.
- You need policies written by non-.NET engineers (platform team, security org) → Rego isolates them from the runtime binary.
- Policy decisions need to span multiple verbs coherently (tenant + role + budget + model-allowlist combined) → Rego's data-join semantics do this naturally.

When OPA is **not** the right choice — skip `Vais.Agents.Control.Policy.Opa` and ship a custom `IAgentPolicyEngine`:

- Pure per-request boolean checks based on in-process state (no tenant, no shared policy library) — a 30-line class that reads a feature flag is simpler.
- Tight latency budgets (sub-500 μs per call) — OPA over HTTP adds 1–5 ms even on loopback. If the agent's entire invoke path is budget-constrained to 10 ms, the percentage overhead is real.
- Policies derived entirely from a host-owned database that already speaks .NET — wrap the DB query as an `IAgentPolicyEngine` directly; no sidecar indirection.

## Limitations (v0.14)

- **No policy version logging by default on bundles.** `LogPolicyVersionOnStartup: true` queries OPA's `/v1/status` on the first evaluation and logs the loaded bundle metadata. Not fired on hot-reload — consumers running bundle servers read the bundle-specific telemetry from their OPA instance.
- **Policy-change propagation is consumer-owned.** The engine caches decisions for 5 seconds regardless of whether the Rego underneath changed. Callers running an OPA bundle server + shipping a new policy mid-day should expect up to 5 seconds of decision-cache overlap with the old policy. Trim `DecisionCacheTtl` if the overlap matters.
- **Adapter doesn't distribute Rego.** OPA itself does that — via `--watch`, bundle server, `kubectl rollout`. The adapter is a pure caller.
- **No native tracing integration with OPA's decision log.** The adapter emits a span; OPA can emit its own decision log. Cross-correlation by `input.schemaVersion + operation + agent.id + timestamp` — not by a single trace id.

## See also

- [Gate agents with OPA](../guides/gate-agents-with-opa.md) — end-to-end: run OPA locally, write Rego, register the engine, observe denials.
- [Author a Rego policy against the VAIS input schema](../guides/author-a-rego-policy-against-the-vais-input-schema.md) — the four guard patterns.
- [Wire a sidecar OPA against the operator](../guides/wire-a-sidecar-opa-against-the-operator.md) — combined v0.13 + v0.14 K8s deployment.
- [Control plane concept](control-plane.md) — where `IAgentPolicyEngine` fits in the verb set.
- `contracts/opa-input-schema.md` — the authoritative v1 input shape + schema-evolution protocol.
- `samples/opa-policies/` — three ready-to-use Rego starters.
- `samples/opa-sidecar/README.md` — the Kubernetes deployment overlay pattern.
