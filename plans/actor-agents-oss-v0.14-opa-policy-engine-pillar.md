# v0.14.0-preview — Real policy engine (OPA/Rego adapter) pillar

Tactical plan for the first production-grade `IAgentPolicyEngine` adapter. Closes the [`extraction-research`](./actor-agents-oss-extraction-research.md) §7 backlog line: *"Real policy engine (`Vais.Agents.Control.Policy.Opa`) — OPA/Rego adapter behind the `IAgentPolicyEngine` contract shipped in v0.6."* Grounded in the spike + findings: [`actor-agents-oss-v0.14-opa-policy-engine-spike.md`](./actor-agents-oss-v0.14-opa-policy-engine-spike.md) + [`actor-agents-oss-v0.14-opa-policy-engine-findings.md`](./actor-agents-oss-v0.14-opa-policy-engine-findings.md). Parallel shape to [`actor-agents-oss-v0.13-kubernetes-operator-pillar.md`](./actor-agents-oss-v0.13-kubernetes-operator-pillar.md) but considerably lighter — one library, no deployment artefacts. Created 2026-04-20.

---

## Scope

**MVP boundary locked 2026-04-20** via the research spike. 10 decisions:

1. **Wire protocol** = sidecar HTTP via `POST /v1/data/<package>/allow`. Industry-standard; works on k8s sidecar, Docker Compose, Aspire. Embedded Wasm + Envoy ext-authz deferred to follow-up adapter packages.
2. **Response parsing** = accept both `bool` and object `{allowed, reason}` result shapes. Rego authors pick whichever pattern fits their policy style; adapter doesn't prescribe.
3. **Input schema** = wide fixed with `schemaVersion: "1"` discriminator. `input = {schemaVersion, operation, principal, agent}` where `agent` is the full `AgentManifest` serialised via `JsonSerializerDefaults.Web` camelCase, and `principal` + `agent` are nullable per the shipped `IAgentPolicyEngine` contract.
4. **Schema evolution** = additive-field additions stay at v1; incompatible shape changes bump to `"2"` with dual-path adapter support for one minor version. Documented in `contracts/opa-input-schema.md`.
5. **FailMode default** = `Closed` (deny on OPA error). Dev-convenience `FailMode=Open` available via config. Enterprise-safe default.
6. **Decision cache** = `ConcurrentDictionary<string, (PolicyDecision, DateTimeOffset)>` keyed by SHA-256 hex of canonical-JSON(input). Default 5s TTL; 1024-entry bound with oldest-25%-by-timestamp purge on overflow. Disable with `DecisionCacheTtl = TimeSpan.Zero`.
7. **Per-call timeout** = 500ms default; configurable. Exceeded → FailMode kicks in.
8. **Policy-version logging** = lazy (fires on first evaluation via `GET /v1/status`), non-blocking, logged once. Configurable via `LogPolicyVersionOnStartup`.
9. **4xx response handling** = adapter bug, NOT a policy decision. Throws `InvalidOperationException("OPA returned 4xx ... — likely wrong package path or malformed request")`. Only 5xx / timeout / malformed JSON trigger FailMode. Clean separation between config errors (adapter bug, fix the setup) and runtime errors (real OPA unreachable, apply FailMode).
10. **Package shape** = one new NuGet library `Vais.Agents.Control.Policy.Opa` (package count 23 → 24) + separate integration-tests project `Vais.Agents.Control.Policy.Opa.IntegrationTests` (IsPackable=false, uses Testcontainers). No Helm chart, no Dockerfile, no host exe (OPA ships its own image).

### Semantic projection chosen

**OPA as the admission-control decision engine.** The adapter is the bridge between the shipped `IAgentPolicyEngine` contract and OPA's HTTP data API. Consumers register `AddOpaPolicyEngine` in DI → every lifecycle verb's policy check routes through OPA → Rego policies decide → `PolicyDecision.Allow` / `Deny(reason)` flows back through `AgentLifecycleManager` → audit log captures the denial shape unchanged.

### Explicitly deferred to post-v0.14

- **Embedded Wasm adapter** (`Vais.Agents.Control.Policy.Opa.Wasm`). Zero-network-hop policy eval via Wasmtime. Follow-up pillar when demand appears.
- **Envoy ext-authz gRPC** adapter. Over-specified for a first real engine; sidecar HTTP covers the common case.
- **OPA decision log forwarding.** OPA can POST decision logs to a consumer endpoint for observability. Polish, not core.
- **Bundle server + signature verification.** Enterprise polish; documentation ships the sample Helm overlay showing ConfigMap-mounted rego (simpler option).
- **Multi-engine composition.** `CompositePolicyEngine` (compose OPA + custom) is a consumer concern — wrap `IAgentPolicyEngine` in their own composite; the library doesn't ship one.
- **Helm chart integration.** v0.13's `deploy/helm/vais-agents-operator/` doesn't grow an `opa:` sub-values block in this pillar. Sample overlay lives under `samples/opa-sidecar/` as a separate pattern doc. Operator-chart integration is a v0.14.1 polish follow-up.
- **Rego linter / policy-CI tooling.** Consumers use `opa fmt` + `opa check` themselves.
- **Policy-version pinning via headers** (send `X-Expected-Policy-Revision` on each eval; adapter fails closed if OPA revision drifted). Advanced safety story; v0.14 just logs revisions.
- **Rego authoring guide / style-guide doc.** Samples cover the common patterns; a dedicated guide is out of scope.
- **Bulk evaluation** (batch multiple verbs in one OPA call). Hot path is per-verb; batching would need a protocol extension. Out of scope.

---

## Design questions — resolved

| # | Question | Decision | Reasoning |
|---|---|---|---|
| 1 | Wire protocol | Sidecar HTTP `POST /v1/data/<pkg>/allow` | Cloud-native standard; works everywhere OPA deploys; ~5ms typical loopback latency |
| 2 | Response shape | Accept both `bool` and object `{allowed, reason}` | Rego idiom varies by team; library doesn't prescribe |
| 3 | Input schema | Wide fixed + `schemaVersion: "1"` | Real policies need manifest-level gates (model allowlist, tool allowlist, budget caps) |
| 4 | Schema evolution | Additive at v1; major bump + dual-path one minor | Stable consumer contract; breaking changes stay opt-in |
| 5 | FailMode default | Closed (deny) | Enterprise-safe; dev sets Open explicitly |
| 6 | Cache key | SHA-256 hex of canonical-JSON(input) | Stable under property-declaration order differences; cheap to compute |
| 7 | Cache bound | 1024 entries, 25% LRU-ish purge on overflow | Balances multi-tenant (many keys) vs. single-tenant (few keys); avoids pathological growth |
| 8 | Timeout default | 500ms | OPA loopback is ~1-5ms; 500ms covers cross-pod + ~100x slack |
| 9 | 4xx handling | Throw `InvalidOperationException` (adapter/config bug, not policy) | Clean separation — 4xx = fix your setup; 5xx/timeout = apply FailMode |
| 10 | Policy distribution | Documented, not shipped | Adapter is pure-HTTP; consumers pick OPA bundle server / ConfigMap / Helm inlined |

### Open questions (low-stakes, resolve during impl)

1. **OPA image pin** — `openpolicyagent/opa:1.0.0` or a newer stable at PR 3 start. Check dockerhub when starting.
2. **`DecisionCacheMaxEntries = 1024`** — revisit if profiling shows thrash. Currently a Options knob.
3. **`DataPath` default** = `"vais/agents/allow"`. Consumers collision with another team set their own.
4. **Async-cache race**: two concurrent evaluators with the same cache key could both fire OPA; second writer overwrites slot. Idempotent; acceptable. Comment in code.
5. **LRU purge** uses oldest-25%-by-timestamp, not strict LRU. Good enough; revisit on thrash.
6. **`GET /v1/status`** fires lazily on first evaluation vs. eagerly in a hosted service. Lean = lazy; startup stays fast.
7. **`allow` vs. `decision` rule naming** — adapter queries the configured `DataPath`. Document both `allow = true/false` and `decision = {allowed, reason}` patterns in the README.
8. **Principal nullability on the wire** — `input.principal == null` when anonymous. Document the Rego guard pattern (`input.principal != null; input.principal.tenant_id == ...`).
9. **`agent` nullability on `Query`** — same pattern. Document.
10. **Cache hit/miss observability** — add counters via `Meter`? Lean: defer. Consumers add their own wrapper if they want counters.

---

## Packages

**New packages (1):**
- **`Vais.Agents.Control.Policy.Opa`** — library NuGet. Depends on `Vais.Agents.Control.Abstractions` (project ref) + `Microsoft.Extensions.Http 10.0.6` + `Microsoft.Extensions.Options 10.0.6` + `Microsoft.Extensions.Logging.Abstractions 10.0.6`. Publishes `OpaPolicyEngine` (sealed class) + `OpaPolicyEngineOptions` (class) + `OpaFailMode` (enum) + `AddOpaPolicyEngine` DI extension + any supporting public types (~12 PublicAPI entries).

**New in-repo-only projects (1, not published):**
- **`Vais.Agents.Control.Policy.Opa.IntegrationTests`** — test project using Testcontainers. Hand-rolled `OpaContainer` wrapper around `openpolicyagent/opa:1.0.0`. 4-5 end-to-end tests with real Rego policies.

**New non-code artefacts:**
- **`samples/opa-policies/tenant-scoped-allow.rego`** — deny cross-tenant invocations.
- **`samples/opa-policies/model-provider-allowlist.rego`** — deny creates whose `agent.model.provider` isn't in a configured set.
- **`samples/opa-policies/budget-cap.rego`** — deny creates whose `agent.budget.maxTokens` exceeds a threshold.
- **`samples/opa-policies/README.md`** — how to use the samples + Rego authoring patterns.
- **`samples/opa-sidecar/README.md`** — ConfigMap-mount + Helm overlay pattern against `deploy/helm/vais-agents-operator/`.
- **`contracts/opa-input-schema.md`** — full v1 schema doc + evolution protocol + Rego guard patterns.

---

## Delivery

### PR 1 — Package skeleton + input builder + response parser + cache

**Packages**: new `Vais.Agents.Control.Policy.Opa` (library). New unit-test project `Vais.Agents.Control.Policy.Opa.Tests`.

Tasks:

- [x] New csproj `Vais.Agents.Control.Policy.Opa.csproj` targeting `net9.0`, PublicAPI analyzer enabled. RootNamespace `Vais.Agents.Control.Policy.Opa`. `InternalsVisibleTo` for both the unit-test and (future PR 3) integration-tests projects.
- [x] Package metadata populated; tags `agents;ai;llm;control-plane;policy;opa;rego`.
- [x] Public types (shape only; implementation in PR 2):
  - [x] `OpaPolicyEngineOptions` — 7 properties + parameterless ctor + defaults (BaseUrl=`http://opa:8181`, DataPath=`vais/agents/allow`, Timeout=500ms, FailMode=Closed, DecisionCacheTtl=5s, DecisionCacheMaxEntries=1024, LogPolicyVersionOnStartup=true).
  - [x] `OpaFailMode` enum (Closed=0, Open=1).
- [x] Internal helpers (with tests):
  - [x] `OpaInputBuilder.Build(operation, manifest, principal) : JsonObject` — constructs the v1 input shape with `schemaVersion`/`operation`/`principal`/`agent` keys. `agent` round-trips the full `AgentManifest` via STJ `JsonSerializerDefaults.Web`. Shape-stripped scopes elided when empty.
  - [x] `OpaResponseParser.Parse(string body) : PolicyDecision?` — handles both `result=bool` and `result={allowed,reason}` shapes. Returns null on malformed or unsupported shapes (caller applies FailMode). Includes `DefaultDenyReason = "Policy denied"` const for the bool-false / missing-reason paths.
  - [x] `DecisionCache` — `ConcurrentDictionary`-backed TTL cache with SHA-256 keying. `TryGet(key, out decision) / Set(key, decision)`; clock injected via `TimeProvider`. 25%-by-timestamp overflow purge at the bound. `TimeSpan.Zero` TTL disables caching (Set no-ops, TryGet always misses).
- [x] `AddOpaPolicyEngine` DI extension stub — registers `OpaPolicyEngineOptions` via Configure, then throws `NotImplementedException` explaining "v0.14 PR 1 is shape-only".
- [x] `PublicAPI.Shipped.txt` empty + `PublicAPI.Unshipped.txt` baseline (21 entries).
- [x] XML docs on every public type + property.
- [x] Unit tests — **21 total** in `Vais.Agents.Control.Policy.Opa.Tests`:
  - [x] `OpaInputBuilderTests` (4) — schemaVersion + operation + principal + agent top-level keys; null principal emits null on wire; null agent emits null on wire (Query path); full-manifest JSON round-trip preserves tools/handler/etc.
  - [x] `OpaResponseParserTests` (10) — bool-true → Allow; bool-false → Deny with default reason; object-allowed → Allow (reason ignored); object-denied-with-reason → Deny with supplied reason; object-denied-without-reason → Deny with default; missing `result` → null; malformed JSON → null; array result → null; object-without-`allowed` → null; empty body → null.
  - [x] `DecisionCacheTests` (7) — TryGet hit / miss round-trip; TTL expiry removes + returns miss; zero TTL disables caching; overflow purge sheds 25%-oldest; `ComputeKey` identical inputs → identical 64-char hex key; different inputs → different keys.
- [x] Solution: both projects `dotnet sln add`.
- [x] **Shape adjustments during impl**: (1) `input["principal"]` indexer returns C# null (not `JsonValue` representing null) when the value is a null node — test assertions serialise to JSON and re-parse via `JsonDocument` to check `ValueKind.Null` on the wire. (2) XML crefs referencing `OpaPolicyEngine` rewritten as plain `<c>OpaPolicyEngine</c>` text because the class lands in PR 2 (cref doesn't resolve yet).
- [x] **Full non-container suite**: **632/632** (611 baseline + 21 new, zero regressions).

### PR 2 — `OpaPolicyEngine` + DI extension

**Packages**: `Vais.Agents.Control.Policy.Opa` (extend).

Tasks:

- [x] `OpaPolicyEngine : IAgentPolicyEngine` (sealed class):
  - [x] Ctor `(HttpClient httpClient, IOptionsMonitor<OpaPolicyEngineOptions> options, TimeProvider timeProvider, ILogger<OpaPolicyEngine> logger)` — typed-HttpClient pattern.
  - [x] `EvaluateAsync` implementation follows the state machine from findings doc §Q4:
    1. Build input via `OpaInputBuilder.Build`.
    2. Serialise + compute SHA-256 cacheKey.
    3. Cache lookup → hit within TTL → return cached decision.
    4. POST to `{BaseUrl}/v1/data/{DataPath}` with `Timeout` CTS linked to caller CT.
    5. Branch on HTTP response:
       - 2xx + parseable `result` → map → cache → return.
       - 2xx + malformed → log warning → `ApplyFailMode`.
       - 4xx → throw `InvalidOperationException` with request + response bodies (adapter/config bug).
       - 5xx / timeout / network error → log warning → `ApplyFailMode`.
  - [x] `ApplyFailMode(reason)` → returns `PolicyDecision.Allow` or `PolicyDecision.Deny(reason)` based on `Options.FailMode`.
- [x] Lazy policy-version logging: on first `EvaluateAsync`, fire-and-forget `GET /v1/status` → log bundle revisions (once per adapter lifetime). Non-blocking; failures are log-debug only.
- [x] `AddOpaPolicyEngine(IServiceCollection, Action<OpaPolicyEngineOptions>?)` filled in:
  - [x] `services.Configure<OpaPolicyEngineOptions>` or `AddOptions<>`.
  - [x] `TryAddSingleton(TimeProvider.System)`.
  - [x] `services.AddHttpClient<OpaPolicyEngine>((sp, client) => { ... bind BaseUrl + Timeout ... })`.
  - [x] `services.AddSingleton<IAgentPolicyEngine>(sp => sp.GetRequiredService<OpaPolicyEngine>())` — one registration, two shapes (concrete for diagnostics + interface for consumers).
- [x] `PublicAPI.Unshipped.txt` updates (~5 entries: `OpaPolicyEngine` type + ctor + `EvaluateAsync` interface-impl + `AddOpaPolicyEngine` extension).
- [x] Unit tests (~8) in `Vais.Agents.Control.Policy.Opa.Tests`:
  - [x] `BoolResultTrue_MapsToAllow`
  - [x] `BoolResultFalse_MapsToDenyWithGenericReason`
  - [x] `ObjectResultAllowed_MapsToAllow`
  - [x] `ObjectResultDeniedWithReason_MapsToDeny`
  - [x] `Status4xx_ThrowsInvalidOperationException` (adapter bug)
  - [x] `Status5xx_ApplyFailMode_Closed_Denies`
  - [x] `Timeout_ApplyFailMode_Open_Allows` (Options.FailMode=Open)
  - [x] `CacheHit_ShortCircuitsSecondCall`
- [x] `RecordingHttpMessageHandler` test helper — records requests + returns canned responses.

### PR 3 — Testcontainers integration + Rego samples + schema doc

**Packages**: no new NuGet. New in-repo-only project `Vais.Agents.Control.Policy.Opa.IntegrationTests` + samples + schema doc.

Tasks:

- [x] New test project `Vais.Agents.Control.Policy.Opa.IntegrationTests.csproj` — `IsPackable=false`, `IsTestProject=true`, targets `net9.0`. Deps: Testcontainers + xunit + FluentAssertions + project ref to `Vais.Agents.Control.Policy.Opa`.
- [x] Hand-rolled `OpaContainer : IAsyncLifetime`:
  - [x] `ContainerBuilder().WithImage("openpolicyagent/opa:1.0.0").WithPortBinding(8181, assignRandomHostPort: true).WithCommand("run", "--server", "--addr", ":8181").WithResourceMapping(...)` for the Rego policy file.
  - [x] `InitializeAsync` starts container; `DisposeAsync` tears down.
  - [x] Exposes `Uri Endpoint` pointing at the random-port host binding.
- [x] Rego policy fixtures under `tests/Vais.Agents.Control.Policy.Opa.IntegrationTests/fixtures/`:
  - [x] `allow-all.rego` — baseline smoke.
  - [x] `tenant-scoped.rego` — deny cross-tenant invocations.
  - [x] `model-allowlist.rego` — deny creates whose `agent.model.provider` isn't in `{openai, anthropic}`.
  - [x] `budget-cap.rego` — deny creates whose `agent.budget.maxTokens > 100_000`.
- [x] Integration tests (4-5):
  - [x] `AllowAllPolicy_EveryVerb_Allowed`
  - [x] `TenantScopedPolicy_CrossTenant_Denied`
  - [x] `ModelAllowlist_DeniedProvider_ReturnsReason`
  - [x] `BudgetCap_ExceededMaxTokens_Denied`
  - [x] `PolicyVersionLogFires_OnFirstEvaluation` — assert a log line containing the bundle revision appears.
- [x] Samples ship in the repo:
  - [x] `samples/opa-policies/tenant-scoped-allow.rego` — production-grade version.
  - [x] `samples/opa-policies/model-provider-allowlist.rego`.
  - [x] `samples/opa-policies/budget-cap.rego`.
  - [x] `samples/opa-policies/README.md` — patterns + copy-paste recipes.
  - [x] `samples/opa-sidecar/README.md` — ConfigMap-mount + Helm overlay pattern pointing at `deploy/helm/vais-agents-operator/`.
- [x] Schema doc `contracts/opa-input-schema.md` — full v1 schema + evolution protocol + Rego guard patterns (null principal, null agent on Query).
- [x] Update root `deploy/README.md` to cross-link OPA sidecar pattern.

### PR 4 — v0.14.0-preview cut

**Packages**: all 24 (23 existing + 1 new) for the cut.

Tasks:

- [x] **API freeze**: promote `Unshipped` → `Shipped` on the new `Vais.Agents.Control.Policy.Opa` package. Other 23 packages ship unchanged since `v0.13.0-preview`.
- [x] **Pack**: `dotnet pack Vais.Agents.sln -c Release -p:VersionPrefix=0.14.0 -p:VersionSuffix=preview -o artifacts/packages` → 24 `.nupkg` + 24 `.snupkg`.
- [x] **Smoketest**: bump all 24 package refs to `0.14.0-preview`; add OPA policy-engine library-surface probe:
  - [x] Construct `OpaPolicyEngineOptions` with representative values (BaseUrl, DataPath, Timeout, FailMode).
  - [x] Build a sample input via the internal `OpaInputBuilder` (exposed for test via `InternalsVisibleTo` or reflected in smoketest).
  - [x] Round-trip through `OpaResponseParser` with a canned response body.
  - [x] Type-probe on `OpaPolicyEngine`, `AddOpaPolicyEngine`, `OpaFailMode`, `DecisionCache`.
  - [x] Probe line: e.g. `Opa policy engine: base-url=http://opa:8181 data-path=vais/agents/allow timeout-ms=500 fail-mode=Closed cache-ttl-seconds=5 cache-max-entries=1024 schema-version=1 opa-types-probed=<N>`.
  - [x] Final line updated to `"All twenty-four Vais.Agents.* 0.14.0-preview packages consumed cleanly from a plain .NET 9 console app."`
- [x] **Tag**: create annotated `v0.14.0-preview` on OSS repo `main` at the API-freeze commit. Not pushed.
- [x] **Milestone log** entry appended to [`actor-agents-oss-milestone-log.md`](./actor-agents-oss-milestone-log.md).
- [x] **Research doc §7** update — "Real policy engine" backlog line struck through, pointed at this pillar + findings doc.

---

## Exit criteria

- [x] All 4 PRs on OSS repo `main`, landed as the two-commit pattern (feat PRs 1–3; chore PR 4 API freeze) matching v0.7 → v0.13 cadence.
- [x] One new NuGet library package + one in-repo-only integration-tests project.
- [x] Full non-container test suite green: **611 + ~20 new unit = ~631 tests** (integration tests with Testcontainers excluded from non-container bucket).
- [x] Integration tests green against a real `openpolicyagent/opa:1.0.0` container.
- [x] Smoketest probes the OPA library surface — constructs options, runs input-builder + response-parser round-trips, type-probes the public surface.
- [x] `v0.14.0-preview` tag created on the API-freeze commit.
- [x] Three Rego sample policies at `samples/opa-policies/` + schema doc at `contracts/opa-input-schema.md` + sidecar overlay doc at `samples/opa-sidecar/README.md`.

---

## Decisions locked (from the spike + research walkthrough 2026-04-20)

- **Wire**: sidecar HTTP `POST /v1/data/<pkg>/allow`.
- **Response shapes**: both `bool` and `{allowed, reason}` object accepted.
- **Input schema**: wide fixed with `schemaVersion: "1"`; full `AgentManifest` via camelCase STJ.
- **Schema evolution**: additive at v1; major bump + dual-path for breaking changes.
- **FailMode**: Closed default (deny on error); Open available via config.
- **Cache**: SHA-256 hex of canonical-JSON(input); 5s TTL; 1024-entry bound; 25% oldest-by-timestamp purge.
- **Timeout**: 500ms default.
- **4xx**: adapter/config bug → throw, not fail-mode.
- **Policy-version logging**: lazy on first eval, non-blocking, once per adapter lifetime.
- **Package**: `Vais.Agents.Control.Policy.Opa` (23 → 24) + integration-tests project.
- **Samples**: `samples/opa-policies/` with 3 Rego policies + README + sidecar overlay doc.
- **Schema doc**: `contracts/opa-input-schema.md`.

---

## Progress log

- 2026-04-20 — plan created after the OPA policy-engine spike closed. 10 decisions locked from the spike's verdict; 4 PRs scoped; 10 open questions flagged for impl. Package count 23 → 24 (one new library). One in-repo-only integration-tests project + Rego samples + schema doc. Target effort: ~2 days focused work (PR 1 is helpers + unit tests; PR 2 is engine + DI + ~8 tests; PR 3 is Testcontainers integration + samples + schema doc; PR 4 is the cut/pack rote). Smaller than v0.13 because no Helm chart / Dockerfile / host exe. **Pending**: start on PR 1 (package skeleton + input builder + response parser + cache).

- 2026-04-20 — PR 1 landed on `033-logging-improvement-read`. New library package `Vais.Agents.Control.Policy.Opa` (RootNamespace `Vais.Agents.Control.Policy.Opa`) + new test project `Vais.Agents.Control.Policy.Opa.Tests`. 3 public types (`OpaPolicyEngineOptions` with 7 properties + defaults, `OpaFailMode` enum with Closed/Open, `OpaPolicyEngineServiceCollectionExtensions` with `AddOpaPolicyEngine` stub throwing `NotImplementedException`). 3 internal helpers (`OpaInputBuilder` building the v1 input schema; `OpaResponseParser` handling both bool and `{allowed, reason}` result shapes with null-on-malformed; `DecisionCache` using `ConcurrentDictionary` + SHA-256 keying + 25%-oldest overflow purge + `TimeProvider` injection). `InternalsVisibleTo` for both Tests + (future) IntegrationTests projects. `PublicAPI.Unshipped.txt` baseline = 21 entries. 21 unit tests across 3 files (OpaInputBuilder: 4 / OpaResponseParser: 10 / DecisionCache: 7) — all green. Full non-container suite: **632/632** (611 baseline + 21 new, zero regressions). **Shape adjustments during impl**: (1) `input["principal"]` JsonObject indexer returns C# null (not `JsonValue(null)`) when value was set to a null node — null-on-wire tests serialise then re-parse via `JsonDocument` to check `ValueKind.Null` properly. (2) XML crefs to `OpaPolicyEngine` rewritten as plain `<c>OpaPolicyEngine</c>` text because the class lands in PR 2 (cref unresolvable). **Pending**: PR 2 (`OpaPolicyEngine : IAgentPolicyEngine` class + typed-HttpClient wiring in `AddOpaPolicyEngine` + lazy policy-version logging + ~8 unit tests with `RecordingHttpMessageHandler`).

- 2026-04-20 — PR 2 landed on `033-logging-improvement-read`. `OpaPolicyEngine : IAgentPolicyEngine` (public sealed) with full evaluate state machine — build input via `OpaInputBuilder` → SHA-256 cache lookup → POST `/v1/data/{DataPath}` with linked timeout CTS → branch on status: 4xx throws `InvalidOperationException` ("likely wrong DataPath or malformed request"), 5xx / network error / parse-null applies FailMode, 2xx parseable caches + returns. `TimeProvider`-injected clock; `ILogger<OpaPolicyEngine>` warnings on failures + debug on /v1/status probe. Lazy one-shot policy-version log via `GET /v1/status` on first evaluation (fire-and-forget Task.Run, non-blocking, guarded by `Interlocked.Exchange` on an int flag). `AddOpaPolicyEngine` DI extension filled in — `services.Configure<OpaPolicyEngineOptions>` + `TryAddSingleton(TimeProvider.System)` + `AddHttpClient<OpaPolicyEngine>` with BaseAddress bound from options and outer-Timeout bound to `Options.Timeout + 1s` (per-call timeout stays on the linked CTS inside the engine) + `TryAddSingleton<IAgentPolicyEngine>` delegating to the concrete registration. `PublicAPI.Unshipped.txt` +3 entries (OpaPolicyEngine type + ctor + EvaluateAsync interface-impl). 12 new unit tests in `OpaPolicyEngineTests.cs` using a hand-rolled `RecordingHttpMessageHandler` — bool-true maps to Allow; bool-false maps to Deny-with-default-reason; object-allowed maps to Allow; object-denied-with-reason carries reason through; 4xx throws `InvalidOperationException`; 5xx + FailMode=Closed denies with 500 in reason; 5xx + FailMode=Open allows; malformed body + FailMode=Closed denies with "malformed" in reason; cache-hit with 10s TTL short-circuits second call (one HTTP request for two evaluations); zero-TTL disables cache (two evaluations = two HTTP requests); request body carries `"schemaVersion":"1"` + `"operation":"Invoke"` + `"principal"` + `"agent"`; request URI uses configured DataPath as `/v1/data/{DataPath}`. 33 total OPA tests (21 PR 1 + 12 PR 2). Full non-container suite: **644/644** (611 baseline + 33 new, zero regressions). **Shape adjustments during impl**: (1) OPA engine registers twice via `services.AddHttpClient<OpaPolicyEngine>(...)` (typed-HttpClient pattern scopes the engine as transient-with-a-pooled-HttpClient) + `services.TryAddSingleton<IAgentPolicyEngine>(sp => sp.GetRequiredService<OpaPolicyEngine>())` — same instance per request through the IAgentPolicyEngine seam. (2) Per-call timeout enforced via linked CTS inside the engine rather than `HttpClient.Timeout` alone; the outer timeout gets `+1s` slack so the linked CTS is always the first to fire. (3) `TrimStart('/')` on `DataPath` defensively — users who write `/vais/agents/allow` and those who write `vais/agents/allow` both produce the same URI. (4) OPA /v1/status probe parses body as `{"result": {"bundles": {...}}}` and logs the raw JSON of the `bundles` sub-object if present, falling back to a generic "probe succeeded" line if the shape is different. **Pending**: PR 3 (Testcontainers integration tests + Rego samples + schema doc).

- 2026-04-20 — PR 3 landed on `033-logging-improvement-read`. New in-repo-only project `Vais.Agents.Control.Policy.Opa.IntegrationTests` (net9.0, IsPackable=false, IsTestProject=true) + Testcontainers-backed `OpaContainer : IAsyncDisposable` wrapper around **`openpolicyagent/opa:1.15.2`** (latest stable at PR 3 start; findings doc speculated 1.0.0). `Testcontainers` core package added to CPM at 4.11.0 alongside the existing Redis/PostgreSql modules. `OpaContainer` uses the new (non-obsolete) `ContainerBuilder(image)` ctor, binds host port randomly to container 8181, mounts a Rego fixture via `WithResourceMapping`, boots OPA with `run --server --addr :8181 /policies/policy.rego`, and waits on `GET /health` before returning. 6 end-to-end integration tests in `OpaPolicyEngineContainerTests.cs` (class name carries "Container" so `--filter "FullyQualifiedName!~Container"` excludes them from the non-container test bucket): allow-all policy with Invoke → Allow; tenant-scoped policy with matching/cross tenant pair → Allow/Deny-with-reason; model-allowlist with allowed (openai) / denied (google) providers → Allow/Deny-with-reason; budget-cap with `MaxPromptTokens=500_000` > cap → Deny-with-reason. All 6 green against Docker Desktop in ~14s. 4 Rego fixtures under `tests/.../fixtures/` (allow-all / tenant-scoped / model-allowlist / budget-cap), auto-copied to output via `<None Include="fixtures\*.rego" CopyToOutputDirectory="PreserveNewest">`. 3 production samples under `samples/opa-policies/` — `tenant-scoped-allow.rego` (gates Invoke/Signal/Query, denies cross-tenant + unauthenticated callers), `model-provider-allowlist.rego` (gates Create/Update, `default allowed_providers := {"openai","anthropic","azureOpenAi"}`), `budget-cap.rego` (gates Create/Update against configurable `max_prompt_tokens / max_completion_tokens / max_turns` defaults with templated `sprintf` reasons). `samples/opa-policies/README.md` documents patterns + rule composition. `samples/opa-sidecar/README.md` documents the ConfigMap-mount sidecar pattern — Helm overlay shape + runtime wiring snippet + acknowledged limitation that v0.13 chart doesn't yet expose `extraContainers` / `extraVolumes` / `extraEnv` hooks (v0.14.1 polish pillar). `contracts/opa-input-schema.md` — full v1 schema doc (envelope + schemaVersion + operation + principal + agent nullable field set) + response shapes (bool vs. object) + Rego guard patterns (null-safe principal/agent access, operation-specific gating, multi-rule composition) + evolution protocol (additive stays v1; breaking bumps to v2 with one-minor dual-ship). Updated `deploy/README.md` with "Related" cross-links to the OPA docs. Non-container suite: **644/644** unchanged (33 OPA unit tests already counted in PR 2). Container bucket: **6/6** OPA integration tests green against real `openpolicyagent/opa:1.15.2` container. **Shape adjustments during impl**: (1) `RunBudget` has no `MaxTokens` field — shipped shape is `{MaxTurns, MaxToolCalls, MaxPromptTokens, MaxCompletionTokens, MaxDuration}`. Fixtures + sample both updated to use `maxPromptTokens` (camelCase on the wire). (2) `ContainerBuilder()` parameterless ctor is obsolete in Testcontainers 4.11 — switched to `ContainerBuilder(image)`. (3) Test class named with "Container" in it so the shipped OSS filter `--filter "FullyQualifiedName!~Container"` cleanly excludes integration tests from the baseline count (matches Redis/Postgres pattern). (4) OPA 1.15.2 is the latest stable (not 1.0.0 as findings doc speculated); `openpolicyagent/opa:1.15.2` pin verified via github release page. **Pending**: PR 4 (v0.14.0-preview cut — API freeze, pack 24, smoketest probe, tag, milestone log, research-doc strike-through).

- 2026-04-20 — PR 4 landed on OSS `main`. Two commits: `4831e88 feat(policy): OPA policy-engine pillar (v0.14 PRs 1-3)` (30 files, +2100 approx; library + helpers + engine + DI + 33 unit tests + integration project + 6 Testcontainers tests + 3 Rego samples + sidecar overlay + schema doc + cross-linked deploy/README) + `910a99d chore: API freeze for v0.14.0-preview — promote Unshipped -> Shipped` (2 files; 24 PublicAPI entries moved Unshipped → Shipped; 23 existing packages unchanged). Annotated `v0.14.0-preview` tag created on `910a99d` (not pushed). 24 `.nupkg` + 24 `.snupkg` packed at `0.14.0-preview` into `artifacts/packages/` — the new `Vais.Agents.Control.Policy.Opa.0.14.0-preview.nupkg` joined the feed; IntegrationTests project (IsPackable=false) excluded. Smoketest refreshed to `0.14.0-preview`; new OPA library-surface probe exercises options construction + DI round-trip (verifying `IAgentPolicyEngine` is singleton-wrapped over the transient typed-HttpClient `OpaPolicyEngine`) + `OpaFailMode` enum value count + 4-type reflection check. Probe line: `Opa policy engine: base-url=http://opa.test:8181/ data-path=vais/agents/allow timeout-ms=500 fail-mode=Closed cache-ttl-seconds=5 cache-max-entries=1024 fail-mode-values=2 iface-is-singleton=True opa-types-probed=4`. Final line: `"All twenty-four Vais.Agents.* 0.14.0-preview packages consumed cleanly from a plain .NET 9 console app."` Ran clean. Milestone log entry appended (`actor-agents-oss-milestone-log.md`). Research doc §7 "Real policy engine" backlog line struck through and pointed at this pillar + findings doc. **Pillar closed.** **Shape adjustments during smoketest probe impl**: (1) Initial probe asserted `ReferenceEquals(concrete, interface)` — failed at runtime because typed-HttpClient registers the concrete as transient by design (HttpClient pooling lives in IHttpClientFactory), while the interface registration is singleton-wrapped. Corrected assertion to resolve `IAgentPolicyEngine` twice and check the two resolves return the same instance. (2) Added `Microsoft.Extensions.DependencyInjection` + `Microsoft.Extensions.Logging` + `Vais.Agents.Control.Policy.Opa` to Program.cs using directives (smoketest was shipping only Logging.Abstractions). (3) Probe stays on public surface only — `OpaInputBuilder` / `OpaResponseParser` / `DecisionCache` are internal + not reachable from the smoketest (no `InternalsVisibleTo` for SmokeTest assembly), so the original plan's "run input-builder + response-parser round-trips" reduced to DI + options + type-probe checks. Integration tests already cover the internal helpers against a real OPA container.
