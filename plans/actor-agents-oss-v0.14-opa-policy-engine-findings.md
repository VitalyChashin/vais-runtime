# v0.14 OPA policy engine — spike findings

Synthesis of the research spike scoped in [`actor-agents-oss-v0.14-opa-policy-engine-spike.md`](./actor-agents-oss-v0.14-opa-policy-engine-spike.md). Answers Q1–Q5 with evidence. Landing verdict at the bottom.

Created 2026-04-20. **Status**: complete. Q1–Q5 resolved from OPA REST API audit + Testcontainers NuGet audit + adapter state-machine sketch + input-schema draft.

---

## Q1 — Wire protocol to OPA

### REST API shape — confirmed

Request (docs verified):

```http
POST /v1/data/vais/agents/allow HTTP/1.1
Content-Type: application/json

{"input": {"operation": "Invoke", "principal": {...}, "agent": {...}, "schemaVersion": "1"}}
```

Response — **two valid shapes** the adapter must handle:

```json
// Rule: `allow = true` / `allow = false`
{"result": true}

// Rule: `allow = {"allowed": true, "reason": "..."}`
{"result": {"allowed": true, "reason": "clearance level sufficient"}}
```

### Decision (Q1): **sidecar HTTP** — `POST /v1/data/<package>/allow`

Adapter handles both response shapes. If `result` is boolean → `Allow` / `Deny("Policy denied")`. If `result` is object with `allowed` + `reason` fields → `Allow` / `Deny(reason)`. If neither shape → adapter error (fail-mode kicks in).

Embedded Wasm (Wasmtime) and Envoy ext-authz stay deferred to follow-up packages if demand surfaces. Sidecar HTTP is the industry-standard first adapter.

### Port + server flag

OPA default: `opa run -s --addr :8181`. Adapter's `OpaPolicyEngineOptions.BaseUrl` defaults to `http://opa:8181` for the common sidecar-pod case.

---

## Q2 — Policy distribution

### Decision (Q2): **documented-but-not-shipped**

The adapter library is pure-HTTP; it never writes policy files. Consumers wire OPA themselves — standard options are (a) OPA bundle server, (b) ConfigMap-mounted Rego files, (c) Helm-inlined values. All work with the adapter unchanged.

**What ships as samples:**

- `samples/opa-policies/tenant-scoped-allow.rego` — deny cross-tenant invocations.
- `samples/opa-policies/model-provider-allowlist.rego` — deny creates whose `agent.model.provider` isn't in a configured set.
- `samples/opa-policies/budget-cap.rego` — deny creates whose `agent.budget.maxTokens` exceeds a threshold.
- `samples/opa-sidecar/README.md` — documents the ConfigMap-mount + Helm overlay pattern against `deploy/helm/vais-agents-operator/`.

No extra package ships for policy distribution.

---

## Q3 — Input schema Rego policies see

### Schema — v1 locked

```json
{
  "schemaVersion": "1",
  "operation": "Create|Invoke|Signal|Query|Cancel|Update|Evict",
  "principal": {
    "id": "sub-claim-string",
    "tenantId": "optional-string-or-null",
    "scopes": ["optional", "array", "or", "null"]
  },
  "agent": {
    "id": "agent-id",
    "version": "v1",
    "handler": { "typeName": "...", "assemblyName": null },
    "protocols": [{"kind": "Http", "endpoint": null}],
    "tools": [{"name": "weather", "source": null}],
    "memory": null,
    "identity": null,
    "autoscaling": null,
    "description": null,
    "labels": {},
    "model": null,
    "systemPrompt": null,
    "mcpServers": null,
    "guardrails": null,
    "handoffs": null,
    "budget": null,
    "contextProviders": null,
    "outputSchema": null,
    "agentMode": "ToolCalling",
    "reasoning": null,
    "observability": null,
    "annotations": null
  }
}
```

**Key properties:**

- `schemaVersion: "1"` — additive field additions stay at v1; incompatible shape changes bump to `"2"` + dual-path support for one minor version.
- `agent` is the full shipped `AgentManifest` serialised via `JsonSerializerDefaults.Web` (camelCase).
- `agent` is **null on `Query`** for unknown agents (consistent with `IAgentPolicyEngine.EvaluateAsync`'s nullable parameter).
- `principal` is **null when caller is anonymous** (again consistent with the interface contract). Rego authors write `input.principal != null; input.principal.tenant_id == ...` style guards.

### Decision (Q3): **wide fixed with `schemaVersion: "1"` discriminator**

Consumers get manifest-level gating (model allowlist, tool allowlist, budget caps). Schema evolution is additive-friendly; breaking changes go through the version field.

The schema doc ships at `contracts/opa-input-schema.md` in the package repo.

---

## Q4 — Failure / caching / timeout

### State machine

```
EvaluateAsync(op, manifest, principal):
  1. Build input: { schemaVersion, operation, principal, agent }
  2. Compute cacheKey = SHA-256(canonical-JSON(input))
  3. Cache lookup:
       - If cached && now - cachedAt < TTL → return cached decision
  4. POST http://opa:8181/v1/data/<package>/allow
     with timeout = Options.Timeout (default 500ms)
  5. Branch on outcome:
     - 200 OK + parseable result:
         decision = map(result)
         cache[cacheKey] = (decision, now)
         return decision
     - 2xx with unparseable / missing `result`:
         log warning + apply FailMode
     - 4xx:
         throw InvalidOperationException("Adapter bug or misconfigured OPA package path")
         // 4xx means the adapter sent malformed request; not a policy decision.
     - 5xx / timeout / network error:
         log warning + apply FailMode

map(result):
  - bool true  → PolicyDecision.Allow
  - bool false → PolicyDecision.Deny("Policy denied")
  - object {allowed:true, ...}    → PolicyDecision.Allow
  - object {allowed:false, reason:"..."} → PolicyDecision.Deny(reason)
  - object {allowed:false}        → PolicyDecision.Deny("Policy denied")
  - anything else → apply FailMode
```

### Defaults — locked

```csharp
public sealed class OpaPolicyEngineOptions
{
    public Uri BaseUrl { get; set; } = new("http://opa:8181");
    public string DataPath { get; set; } = "vais/agents/allow";  // maps to POST /v1/data/vais/agents/allow
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMilliseconds(500);
    public OpaFailMode FailMode { get; set; } = OpaFailMode.Closed;  // deny on error
    public TimeSpan DecisionCacheTtl { get; set; } = TimeSpan.FromSeconds(5);
    public int DecisionCacheMaxEntries { get; set; } = 1024;
    public bool LogPolicyVersionOnStartup { get; set; } = true;
}

public enum OpaFailMode
{
    Closed = 0,  // Deny on error (safe default)
    Open = 1,    // Allow on error (dev convenience)
}
```

### Decision (Q4): **FailMode=Closed default, 5s cache TTL, 500ms timeout, policy-version log on startup**

All configurable. `DecisionCacheTtl = TimeSpan.Zero` disables cache entirely.

### Cache design

- Simple `ConcurrentDictionary<string, (PolicyDecision, DateTimeOffset)>` keyed by SHA-256 hex of canonical-JSON(input).
- LRU-ish bounded eviction: when count exceeds `DecisionCacheMaxEntries`, clear oldest 25%. Cheap + avoids pathological memory growth.
- TTL check on read: stale → re-evaluate.

---

## Q5 — Package shape + testing

### Package — locked

New library package: **`Vais.Agents.Control.Policy.Opa`**.

Dependencies:
- `Vais.Agents.Control.Abstractions` (project ref)
- `Microsoft.Extensions.Http 10.0.6` (typed HttpClient pattern)
- `Microsoft.Extensions.Options 10.0.6` (IOptionsMonitor)
- `Microsoft.Extensions.Logging.Abstractions 10.0.6`

Public surface (~12 entries expected):
- `OpaPolicyEngine : IAgentPolicyEngine` (sealed class)
- `OpaPolicyEngineOptions` (class, 7 properties + parameterless ctor)
- `OpaFailMode` (enum, 2 values)
- `AddOpaPolicyEngine(IServiceCollection, Action<OpaPolicyEngineOptions>?)` (DI extension)

Internal:
- `OpaInputBuilder` — builds the wide-fixed schema from (operation, manifest, principal)
- `OpaResponseParser` — handles both bool + object response shapes
- `DecisionCache` — SHA-256-keyed TTL cache
- `OpaPolicyEngineDiagnostics` — policy-version log on startup via a separate `GET /v1/status` call (best-effort)

### Testing

Unit tests (`Vais.Agents.Control.Policy.Opa.Tests`):
- `RecordingHttpMessageHandler` — stubs HTTP responses; covers bool-result, object-result, 4xx, 5xx, timeout, malformed-JSON paths.
- `DecisionCacheTests` — cache hits/misses, TTL expiry, bounded eviction.
- `OpaInputBuilderTests` — schema shape regression guard (spec JSON round-trips match `contracts/opa-input-schema.md`).

Integration tests (`Vais.Agents.Control.Policy.Opa.IntegrationTests` — new project, uses Testcontainers):
- `Testcontainers` has **no shipped `Testcontainers.Opa` module** (NuGet-audited 2026-04-20). Hand-roll thin wrapper `OpaContainer : IAsyncLifetime` using generic `ContainerBuilder().WithImage("openpolicyagent/opa:1.0.0").WithPortBinding(8181, assignRandomHostPort: true).WithCommand("run", "--server", "--addr", ":8181")` + mount a Rego policy file via `.WithResourceMapping(...)`.
- 4-5 tests exercising real end-to-end Rego evaluation.

### Package count: **23 → 24**

---

## Verdict — ready to write the pillar plan

### Locked decisions

1. **Wire** = sidecar HTTP via `POST /v1/data/<package>/allow`.
2. **Response parsing** = accept both `bool` and object `{allowed, reason}` result shapes.
3. **Input schema** = wide fixed with `schemaVersion: "1"` discriminator; `agent` is full `AgentManifest` serialised via `JsonSerializerDefaults.Web`; `principal` + `agent` nullable per the shipped contract.
4. **FailMode default** = `Closed` (deny on error).
5. **Decision cache** = `ConcurrentDictionary` keyed by SHA-256(canonical-JSON(input)); default 5s TTL; 1024-entry bound with 25% LRU-ish purge.
6. **Per-call timeout** default = 500ms.
7. **Policy-version log on startup** = opt-in on by default; `GET /v1/status` best-effort.
8. **Package** = one new library `Vais.Agents.Control.Policy.Opa` (23 → 24); test project with Testcontainers-based integration tests.
9. **Policy distribution** = documented-but-not-shipped. Rego samples at `samples/opa-policies/` + sidecar overlay doc.
10. **4xx responses** = adapter bug — throw `InvalidOperationException`, NOT fail-mode. 4xx means the request was malformed (wrong package path, invalid JSON). Consumers hit it in dev; prod should never see it.

### Proposed PR shape (4 PRs)

**PR 1 — Package skeleton + input builder + response parser + cache.**
- New csproj `Vais.Agents.Control.Policy.Opa` (library, net9.0, PublicAPI analyzer).
- `OpaPolicyEngineOptions` + `OpaFailMode`.
- Internal `OpaInputBuilder` / `OpaResponseParser` / `DecisionCache`.
- Unit tests for each helper (~12 tests).
- PublicAPI baseline.
- No `OpaPolicyEngine` class yet.

**PR 2 — `OpaPolicyEngine` + DI extension.**
- `OpaPolicyEngine : IAgentPolicyEngine` — typed-HttpClient-backed.
- `AddOpaPolicyEngine(IServiceCollection, Action<OpaPolicyEngineOptions>?)` DI extension.
- Startup policy-version log.
- Unit tests with `RecordingHttpMessageHandler` covering bool-result, object-result, 4xx (→ throw), 5xx (→ fail-mode), timeout, malformed JSON, cache-hit path (~8 tests).
- PublicAPI updates.

**PR 3 — Testcontainers integration tests + Rego samples + input-schema doc.**
- New test project `Vais.Agents.Control.Policy.Opa.IntegrationTests` (IsPackable=false).
- Hand-rolled `OpaContainer` wrapper around `openpolicyagent/opa:1.0.0`.
- 4-5 end-to-end tests: allow-all / deny-by-tenant / model-allowlist / budget-cap / version-log.
- `samples/opa-policies/{tenant-scoped-allow,model-provider-allowlist,budget-cap}.rego`.
- `samples/opa-policies/README.md` + `samples/opa-sidecar/README.md` (ConfigMap + Helm overlay pattern).
- `contracts/opa-input-schema.md` — full v1 schema doc + evolution protocol.

**PR 4 — v0.14.0-preview cut.**
- API freeze on the new package (Unshipped → Shipped).
- Pack 24 packages at `0.14.0-preview`.
- Smoketest bump + OPA library-surface probe (construct `OpaPolicyEngine` with a test handler, verify DI registrations, round-trip input-builder).
- Tag `v0.14.0-preview`.
- Milestone log + research doc §7 strike-through.

### Effort estimate

**4 PRs, ~2 days focused work.** Smaller than v0.13 because no new deployment artefacts (no Helm chart, no Dockerfile, no CRD YAML). Largest PR is PR 2 (policy engine + cache integration + ~8 tests); PR 3 (Testcontainers) is medium.

### Non-goals for v0.14

- **Embedded Wasm adapter.** Future `Vais.Agents.Control.Policy.Opa.Wasm` package if someone needs zero-network-hop policy eval. Deferred.
- **Envoy ext-authz.** Overspecified; sidecar HTTP covers the ask.
- **OPA decision log forwarding.** OPA can POST decision logs to a consumer endpoint; observability polish, not core. Deferred.
- **Bundle server + signature verification.** Enterprise polish; documentation ships with the sample Helm overlay.
- **Multi-engine composition** (compose OPA + custom engine). Out of scope — consumers compose via wrapper `IAgentPolicyEngine`.
- **Helm chart sidecar pattern wired into `deploy/helm/vais-agents-operator/`.** Sample overlay documented, not merged into the operator chart. Follow-up polish pillar.
- **Rego-linter / policy-CI shipped as tooling.** Out of scope — consumers use `opa fmt` + `opa check` themselves.

---

## Open items (for pillar planning, not blockers)

1. **OPA image pin** — `openpolicyagent/opa:1.0.0` vs. `:latest` vs. `:0.70.0`. Pin to a specific tag during PR 3 when running the integration tests. Check dockerhub for the latest stable at that moment.
2. **`DecisionCacheMaxEntries = 1024` default** — is this right? Most tenants have <100 active agents × 7 verbs × ~5 principals = ~3500 potential keys. Under-sized for multi-tenant; over-sized for single-tenant. Lean: 1024 balances both + Options.DecisionCacheMaxEntries is a knob.
3. **Input schema includes `JsonElement` fields** (`agent.outputSchema`, `agent.reasoning`, etc.). These serialise fine but can bloat input payloads. Lean: ship as-is; document that consumers can shrink by stripping manifest fields in a pre-processor if performance demands.
4. **Async-cache race** — two concurrent evaluators with the same cache key could both fire OPA requests. Acceptable — duplicates are idempotent on OPA side; the second writer just overwrites the cache slot. Worth a comment in the code.
5. **Cache LRU purge** uses oldest-25%-by-timestamp. Not strict LRU (no access-time tracking). Good enough for most workloads; revisit if profiling shows hot-key thrashing.
6. **`DataPath` default** = `"vais/agents/allow"`. Consumers who collide with another team's `vais/*` namespace set their own path. Document the convention in the README.
7. **Startup version log** — should the `GET /v1/status` call block adapter startup, or fire async? Lean: fire async on first evaluation (lazy), log the result once. Non-blocking startup.
8. **`allow` rule vs. `deny` rule** — some Rego conventions prefer `default deny = false; deny[reason] { ... }` pattern. Our adapter only queries `<path>/allow`; consumers who want deny-list patterns either wire `allow = not deny[_]` or configure `DataPath: "<path>/decision"` pointing at a rule that returns the structured object. Document both patterns.
