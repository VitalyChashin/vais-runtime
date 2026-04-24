# v0.14 Real policy engine (OPA/Rego adapter) — research spike

Scoped research pass before committing to a v0.14 pillar plan. Companion to [`actor-agents-oss-extraction-research.md`](./actor-agents-oss-extraction-research.md) §7 backlog: *"Real policy engine (`Vais.Agents.Control.Policy.Opa`) — OPA/Rego adapter behind the `IAgentPolicyEngine` contract shipped in v0.6."* Ships the first production-grade adapter for the admission-control hook that v0.6 left contract-only. Created 2026-04-20.

---

## Why a spike before a pillar

The v0.6 control-plane pillar shipped `IAgentPolicyEngine` + `PolicyDecision` + `PolicyOperation` as contracts only — the default is `NullAgentPolicyEngine.AllowAll`, and no real engine ships. OPA/Rego is the obvious first real implementation (cloud-native standard; used by Kubernetes admission controllers, Envoy, Gatekeeper, Conftest). But several design choices are costly to reverse post-freeze:

- **Wire protocol** — HTTP sidecar is industry-standard but not the only option; embedded Wasm works for single-process deployments. Whichever we pick first anchors the ecosystem.
- **Input schema** — what Rego policies get to see. Narrow vs. wide; fixed vs. evolving. Changing the shape after consumers write policies is expensive.
- **Failure mode** — fail-open or fail-closed when OPA unreachable. Production default matters.
- **Package shape** — one library? library + Helm samples? Testcontainers integration?

Spike output: findings doc + 5 locked decisions + PR shape. No public surface change, no package bumps, no tag.

---

## Current state (confirmed before spike)

Verified as of 2026-04-20 (`v0.13.0-preview` on OSS `main`):

- **Policy contract** (`Vais.Agents.Control.Abstractions`): `IAgentPolicyEngine.EvaluateAsync(PolicyOperation, AgentManifest?, AgentPrincipal?, CT) : ValueTask<PolicyDecision>`. `PolicyDecision` is a readonly record struct with `IsAllowed` + `Reason` + static `Allow` / `Deny(reason)` factories. `PolicyOperation` = 7-verb enum (Create / Invoke / Signal / Query / Cancel / Update / Evict).
- **Default engine**: `NullAgentPolicyEngine.AllowAll` in `Vais.Agents.Control.Abstractions` — allow-everything stub. Real engines slot in via DI.
- **Audit integration**: `AgentLifecycleManager` (in `Vais.Agents.Control.InProcess`) calls `policyEngine.EvaluateAsync(...)` before every verb; on deny, writes an `AuditLogEntry{Allowed=false, DenyReason=...}` and throws `AgentPolicyDeniedException`. Audit path already routes denials cleanly.
- **Principal flow**: `IPrincipalMapper` + `AgentContextAccessor` push the `AgentPrincipal` through middleware; policy engine sees the same principal the request authenticated as.
- **Zero OPA code**: no existing dep on `Open Policy Agent` / Rego / Wasmtime. Greenfield.

---

## Five blocking questions

1. **Q1 — Wire protocol to OPA.** Three candidates:
   - **(a) Sidecar HTTP** — OPA binary runs as a separate process (pod sidecar, host service, Docker daemon). Adapter POSTs `{"input": {...}}` to `http://opa:8181/v1/data/<package>/allow`. Typical 1-5ms latency (loopback), ~15-50ms cross-pod. Standard cloud-native idiom.
   - **(b) Embedded Wasm** — OPA compiles `.rego` → `.wasm`; .NET hosts via `Wasmtime` / `WasmerSharp`. Zero network hop; evaluation sub-ms. Cons: .NET wasm tooling is less mature than Go/Node; bundle-distribution story requires a separate .wasm build step upstream.
   - **(c) Envoy External Authorization (ext-authz)** — OPA-compatible gRPC protocol originally for Envoy mesh. Overspecified for a .NET library's first real adapter.
   - Lean: **(a) sidecar HTTP** — industry-standard; works equally on k8s sidecar, Docker Compose, Aspire; plays nice with existing OPA deployments; easy to test with Testcontainers. Embedded Wasm stays possible as a follow-up package `Vais.Agents.Control.Policy.Opa.Wasm` if demand appears.

2. **Q2 — Policy distribution.** Who ships Rego files to OPA?
   - **(a) OPA bundle server** — OPA polls `GET /bundles/<name>` periodically; server ships signed tarballs. Enterprise-grade. Requires an extra service component.
   - **(b) ConfigMap-mounted Rego files** — K8s ConfigMap → mounted into OPA sidecar → OPA's `--watch` auto-reloads on mount change. Simple; standard k8s idiom.
   - **(c) Inline Helm values** — chart values carry rego strings; Helm templates them into a ConfigMap.
   - Lean: **documented-but-not-shipped** — the adapter talks HTTP to a pre-configured OPA URL; policy distribution is the operator / sysadmin's concern. Ship `samples/opa-policies/` with tenant-scoped-allow + label-allowlist + budget-cap examples, and a sample docker-compose / helm overlay showing the ConfigMap-mounted pattern. Keep the library focus narrow.

3. **Q3 — Input schema Rego policies see.** Fixed contract or free-form?
   - **(a) Narrow fixed**: `input = {operation, principal: {id, tenantId, scopes?}, agent: {id, version}}`. Small payload; stable; doesn't expose full manifest.
   - **(b) Wide fixed (full manifest)**: `input = {operation, principal, agent: <full AgentManifest JSON>, schemaVersion: "1"}`. Heavier; lets Rego gate on `agent.model.provider`, `agent.tools[].name`, `agent.budget.maxTokens`, etc.
   - **(c) Free-form passthrough** — adapter takes an `Action<IDictionary<string, object>>` input builder; consumers define the shape.
   - Lean: **(b) wide fixed with a `schemaVersion` discriminator** — real-world policies routinely want manifest-level gates ("deny if model.provider is 'anthropic' for tenant-X", "deny if tools includes 'shell'"). Adding fields to a versioned schema is additive; consumers who only care about `operation` + `principal` ignore the extra JSON. Document the schema in `contracts/opa-input-schema.md`.

4. **Q4 — Failure behaviour / caching / timeouts.** Four knobs:
   - **Fail-mode**: what happens when OPA is unreachable / times out / returns malformed. Options: fail-closed (deny) or fail-open (allow). Enterprise-safe default = fail-closed; dev convenience = fail-open. Make it configurable with a safe default.
   - **Decision cache**: TTL-bounded in-memory cache keyed by SHA-256(input JSON). Same input → same decision → short-circuit. Policy reloads on OPA side invalidate caller-side cache after TTL. Default 5s; disable with TimeSpan.Zero.
   - **Per-call timeout**: default 500ms. Exceeded → fail-mode kicks in.
   - **Policy-version logging**: on adapter startup, `GET /v1/status` returns bundle revisions. Log once for observability.
   - Lean: all four configurable via `OpaPolicyEngineOptions`. Default: `FailMode=Closed`, `DecisionCacheTtl=5s`, `Timeout=500ms`, `LogPolicyVersionOnStartup=true`.

5. **Q5 — Package shape + testing.**
   - One new library NuGet package `Vais.Agents.Control.Policy.Opa`. Depends on `Vais.Agents.Control.Abstractions` + `Microsoft.Extensions.Http` + `Microsoft.Extensions.Options`. Zero other deps.
   - Surface: `OpaPolicyEngine : IAgentPolicyEngine` (public sealed), `OpaPolicyEngineOptions` (public), `AddOpaPolicyEngine(IServiceCollection, Action<OpaPolicyEngineOptions>?)` DI extension (public static).
   - Test project: `Vais.Agents.Control.Policy.Opa.Tests`.
     - Unit tests with `RecordingHttpMessageHandler` + canned HTTP responses.
     - Integration tests with `Testcontainers.OPA` (or a thin wrapper around `openpolicyagent/opa:latest` image via the generic `Testcontainers` API). Real Rego policies evaluated end-to-end.
   - Package count: **23 → 24**.
   - Lean: single library + test project. No host exe, no Helm chart, no Dockerfile (OPA ships its own image).

---

## Tasks (research + archetype exercises)

- [x] **Q1 — Wire protocol.** OPA REST API shape confirmed via `openpolicyagent.org/docs/rest-api`: `POST /v1/data/<path>` with `{"input": {...}}` body. Response is `{"result": <boolean-or-object>}` — rule returning `allow = true` yields `{"result": true}`; rule returning `allow = {"allowed": true, "reason": "..."}` yields `{"result": {"allowed": true, "reason": "..."}}`. **Adapter contract: handle both shapes.** Server flag = `opa run -s` (default port 8181).
- [x] **Q2 — Policy distribution sketch.** Three Rego samples sketched for `samples/opa-policies/` — tenant-scoped-allow, model-provider-allowlist, budget-cap. No library code ships for distribution; adapter is pure-HTTP and never writes policy files. Sample docker-compose + Helm overlay shows ConfigMap-mount pattern.
- [x] **Q3 — Input schema draft.** Full v1 schema locked — `{schemaVersion, operation, principal, agent}` where `principal` + `agent` are nullable per the shipped `IAgentPolicyEngine` contract, and `agent` is the full `AgentManifest` via `JsonSerializerDefaults.Web` camelCase. Additive-field evolution stays at v1; incompatible shape changes bump to `"2"` with dual-path support for one minor.
- [x] **Q4 — Failure/caching/timeout walkthrough.** State machine drafted in findings doc — 4xx is adapter bug (throw), 5xx/timeout/malformed is policy-path error (fail-mode kicks in). `ConcurrentDictionary` cache keyed by SHA-256(canonical-JSON(input)) with default 5s TTL + 1024-entry bound + 25% LRU-ish purge. FailMode default = `Closed`.
- [x] **Q5 — Package layout + NuGet audit.** `Testcontainers.Opa` NOT published on NuGet (verified 2026-04-20). Hand-rolled `OpaContainer : IAsyncLifetime` wrapper around generic `ContainerBuilder().WithImage("openpolicyagent/opa:1.0.0").WithCommand("run", "--server", "--addr", ":8181").WithResourceMapping(...)` for Rego files. Package: single new library `Vais.Agents.Control.Policy.Opa` (23 → 24) + separate integration-tests project using Testcontainers.
- [x] **Findings doc.** [`actor-agents-oss-v0.14-opa-policy-engine-findings.md`](./actor-agents-oss-v0.14-opa-policy-engine-findings.md) — Q1–Q5 synthesis + 10 locked decisions + proposed 4-PR pillar shape + ~2-day effort estimate.

---

## Exit criteria

- [x] All five questions answered with evidence (not opinion) — Q1 from OPA REST API docs + response-shape audit; Q2 from sample-Rego sketch; Q3 from schema draft + evolution protocol; Q4 from state-machine sketch + cache design; Q5 from Testcontainers NuGet audit (absent, hand-roll) + image pin.
- [x] Recommendation lands: **ready to write v0.14 pillar plan.** 10 decisions locked in findings doc.

No public surface change. No package bumps. No tag.

---

## Progress log

- 2026-04-20 — spike plan created after design conversation. Five blocking questions scoped (wire protocol, policy distribution, input schema, failure + caching + timeout, package + testing). Lean positions recorded: sidecar HTTP; documented-but-not-shipped distribution; wide fixed input schema with `schemaVersion` discriminator; fail-closed default + 5s cache + 500ms timeout; single library + Testcontainers. Findings doc pending.
- 2026-04-20 — Spike complete. All five leans held up unchanged. Q1: OPA REST API shape confirmed — adapter must accept both boolean and object `{allowed, reason}` result shapes. Q2: zero distribution code in library; samples + overlay docs only. Q3: wide fixed schema with `schemaVersion: "1"` + full `AgentManifest` via camelCase STJ; evolution protocol documented. Q4: fail-closed default; `ConcurrentDictionary` cache with 5s TTL + 1024-entry bound + LRU-ish 25% purge; 500ms timeout default; 4xx = adapter bug (throw), not policy-path. Q5: `Testcontainers.Opa` absent from NuGet — hand-roll thin wrapper around `openpolicyagent/opa:1.0.0` via generic ContainerBuilder. Findings doc landed with 10 locked decisions + proposed 4-PR pillar shape (PR 1 package skeleton + helpers; PR 2 engine + DI; PR 3 Testcontainers integration tests + Rego samples + schema doc; PR 4 v0.14.0-preview cut). Effort estimate: ~2 days. One new library package. **Ready to write v0.14 pillar plan.**
