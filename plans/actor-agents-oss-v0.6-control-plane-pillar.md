# v0.6.0-preview — Control-plane pillar (deferred post-v0.4 §9.8 item)

Lightweight tactical plan for the HTTP control plane — turning the 14 `AgentManifest` + `IAgentLifecycleManager` contracts shipped contract-only in v0.4 into a working "kubectl-for-agents" surface. Companion to [`actor-agents-oss-architecture-review.md`](./actor-agents-oss-architecture-review.md) §5.3 and the post-v0.5 backlog bullet in [`actor-agents-oss-extraction-research.md`](./actor-agents-oss-extraction-research.md) §7. Created 2026-04-19.

---

## Scope

**MVP boundary locked 2026-04-19** (narrower than the full architecture-review sketch; items explicitly deferred below):

1. **HTTP REST API** over the 7 lifecycle verbs. Not gRPC in MVP (deployment friction) — gRPC is a follow-up.
2. **ASP.NET Core minimal API** server + typed HTTP client, both as separate NuGet packages.
3. **Orleans-backed `IAgentLifecycleManager`** + **in-process variant** for embedded/sidecar deployments. Single-cluster only; multi-region deferred.
4. **YAML manifest loader** — pure `IAgentManifestLoader` that parses `AgentManifest` from files/directories, returns `IReadOnlyList<AgentManifest>`. Reconciliation loops live in the consumer, not the loader.
5. **`IAgentPolicyEngine` abstraction** shipped with a no-op default. Real OPA/Rego integration is a separate package, post-v0.6.
6. **Identity: JWT bearer validation only** for inbound; one OIDC issuer binding; `OutboundCredential` resolved via secret-ref + OAuth2 client-credentials for the outbound path. SPIFFE deferred.
7. **Control-plane observability** — per-verb latency, error rates, audit log of Create/Update/Evict with caller principal. Reuses OTel GenAI + `vais.*` semantic conventions.
8. **Streaming Invoke via SSE**. WebSockets deferred (SSE matches how LLM provider SDKs surface token streams already; simpler operational story).
9. **Explicitly deferred** to post-v0.6:
   - Kubernetes CRDs + operator (own package `Vais.Agents.Control.KubernetesOperator`).
   - Real policy engine (`Vais.Agents.Control.Policy.Opa`).
   - CLI (`vais apply / get / invoke / logs / signal`).
   - Multi-region + cross-cluster routing.
   - In-flight version migration (rolling/canary/blue-green).
   - gRPC API.
   - SPIFFE / cert-based outbound identity.

---

## New packages

- `Vais.Agents.Control.Abstractions` (new) — `IAgentPolicyEngine`, `PolicyDecision`, audit-log contract. Core remains Orleans-free.
- `Vais.Agents.Control.Orleans` (new) — Orleans-backed `IAgentLifecycleManager`, wraps `OrleansAgentRuntime` + registry + session/config/journal grains.
- `Vais.Agents.Control.InProcess` (new) — `InMemoryAgentLifecycleManager` for sidecar/embedded hosts.
- `Vais.Agents.Control.Http.Server` (new) — ASP.NET Core minimal API + `AddAgentControlPlane()` DI extensions.
- `Vais.Agents.Control.Http.Client` (new) — typed `IAgentControlPlaneClient` over `HttpClient`.
- `Vais.Agents.Control.Manifests.Yaml` (new) — `IAgentManifestLoader` + YAML parser (`YamlDotNet` dep).

Total: **6 new packages**. Brings the OSS ship count from 13 → 19.

---

## Delivery

### PR 1 — Lifecycle-manager engine + policy scaffolding

**Packages**: `Vais.Agents.Abstractions`, `Vais.Agents.Control.Abstractions` (new), `Vais.Agents.Control.Orleans` (new), `Vais.Agents.Control.InProcess` (new).

Tasks:

- [ ] Control.Abstractions: `IAgentPolicyEngine` + `PolicyDecision` (`Allow` / `Deny(reason)`) + `PolicyOperation` enum (`Create`/`Invoke`/`Signal`/`Update`/`Cancel`/`Evict`/`Query`). `AuditLogEntry` record (At, Operation, AgentId, PrincipalId, Outcome). `IAuditLog` interface with `AppendAsync`.
- [ ] Control.Abstractions: `NullAgentPolicyEngine.Instance` (always Allow), `NullAuditLog.Instance` (no-op). Defaults match pre-v0.6 behaviour when unset.
- [ ] Control.Orleans: `OrleansAgentLifecycleManager : IAgentLifecycleManager`. Create wraps `AgentManifest` → `IAgentRegistry.Register` + agent-grain activation. Invoke routes through `OrleansAgentRuntime.GetOrCreate(agentId).AskAsync`. Signal routes through a new `IAgentLifecycleGrain.SignalAsync` (forwards to the agent's active run via the journal's RunId association). Cancel/Evict/Update/Query map to the relevant grain methods. Each verb wraps `IAgentPolicyEngine.EvaluateAsync` as a middleware layer; `IAuditLog.AppendAsync` after success or denial.
- [ ] Control.InProcess: `InMemoryAgentLifecycleManager` — same contract, backed by `InMemoryAgentRuntime` + `InMemoryAgentRegistry`. For the ASP.NET-Core-only embedded deployment path (no Orleans cluster).
- [ ] Tests — 12 new across Control.Abstractions.Tests + Control.Orleans.Tests: every verb happy-path, policy deny short-circuits and audits, policy allow runs the verb, null policy + null audit preserves v0.4 behaviour, Cancel cancels in-flight run, Evict removes manifest, Query returns current `AgentStatus`.
- [ ] `PublicAPI.Unshipped.txt` updates.

Breaking-change ledger: additive contracts only. `IAgentLifecycleManager` shipped in v0.4 was contract-only — no existing implementation to break.

### PR 2 — YAML manifest loader

**Packages**: `Vais.Agents.Control.Manifests.Yaml` (new).

Tasks:

- [ ] `IAgentManifestLoader` interface in Control.Abstractions — `LoadFromStringAsync(yaml)`, `LoadFromFileAsync(path)`, `LoadFromDirectoryAsync(dir, pattern)` all returning `IReadOnlyList<AgentManifest>`. Validation errors throw `AgentManifestValidationException(ImmutableArray<string> errors)`.
- [ ] `YamlAgentManifestLoader : IAgentManifestLoader` — `YamlDotNet` under the hood, one document per manifest, `---` separator for multi-manifest files. Schema: flat mapping onto `AgentManifest` + sub-records; `labels` as inline map; `tools`/`memory`/`identity` as string refs resolved lazily at Invoke time.
- [ ] Validation: required fields (`id`, `version`, `handler.name`), label key shape (`[a-z0-9][a-z0-9-_.]*`), semver-ish version format (`major.minor` minimum), no-duplicate-ids across a batch.
- [ ] `IAgentManifestLoader` stays pure — doesn't reconcile against a registry. Reconciliation is the caller's job (HTTP server, CLI, operator).
- [ ] Tests — 10 new: single-manifest happy path, multi-manifest with `---` separators, every optional field round-trips, missing required fields throws with clear error list, invalid label keys rejected, duplicate ids within a batch rejected, file + directory loaders, schema version mismatch.
- [ ] `PublicAPI.Unshipped.txt` updates.

### PR 3 — HTTP server + client

**Packages**: `Vais.Agents.Control.Http.Server` (new), `Vais.Agents.Control.Http.Client` (new).

Tasks:

- [ ] Server: ASP.NET Core minimal-API surface mapping the 7 verbs + list/search. Endpoints:
  - `POST /v1/agents` (Create: `AgentManifest` body → `AgentHandle`) — **201 Created** + `Location` header.
  - `GET /v1/agents?labels=key=value&version=1.0` (List: filtered) — **200** + paged `AgentManifest[]`.
  - `GET /v1/agents/{id}` (Query: → `AgentHandle` + `AgentStatus`) — **200** / **404**.
  - `PATCH /v1/agents/{id}` (Update: partial `AgentManifest` body).
  - `DELETE /v1/agents/{id}?mode=cancel|evict` (Cancel vs Evict).
  - `POST /v1/agents/{id}/invoke` (Invoke: `AgentInvocationRequest` → `AgentInvocationResult`).
  - `POST /v1/agents/{id}/invoke/stream` (SSE streaming Invoke — text deltas).
  - `POST /v1/agents/{id}/signal` (Signal: `AgentSignal` body).
- [ ] Error model: RFC 7807 Problem Details for 4xx/5xx. `type` urns carry Vais-specific codes (`urn:vais-agents:policy-denied`, `urn:vais-agents:manifest-invalid`, etc.).
- [ ] Idempotency: `Idempotency-Key` header on Create/Invoke/Signal. Server-side dedupe by `(caller-principal, idempotency-key)` → cached outcome for 24h (in-memory for MVP; Redis-backed later).
- [ ] OpenAPI: auto-generated via ASP.NET Core's built-in `AddOpenApi()`; served at `/openapi/v1.json`.
- [ ] DI: `services.AddAgentControlPlane()` wires default registry, lifecycle manager (chose Orleans or InProcess at registration), policy engine, audit log. `app.MapAgentControlPlane("/v1")` registers the routes.
- [ ] Client: `IAgentControlPlaneClient` typed interface mirroring the 7 verbs + list. Default impl wraps `HttpClient`; optional `Idempotency-Key` parameter on every method; streaming Invoke returns `IAsyncEnumerable<string>`.
- [ ] Tests — 18 new (+server WebApplicationFactory integration suite): every verb happy-path, 404 cases, SSE streaming round-trip, Problem Details shape on policy deny, idempotency replay returns cached result, client-side cancellation aborts server-side run.
- [ ] `PublicAPI.Unshipped.txt` updates.

### PR 4 — Identity + auth + observability

**Packages**: `Vais.Agents.Control.Http.Server` (extend), `Vais.Agents.Control.Abstractions` (extend).

Tasks:

- [ ] Inbound: JWT bearer validation via `Microsoft.AspNetCore.Authentication.JwtBearer`. `AddAgentControlPlaneAuth(options)` DI extension takes issuer URL, audience, signing-key discovery mode (OIDC or static). On successful validation, construct `AgentPrincipal` from the `sub`/`tenant_id`/`scope` claims and push onto an `IAgentContextAccessor` scope.
- [ ] Outbound: `SecretRefOutboundCredentialResolver` — resolves `OutboundCredential` values from `IConfiguration` / `IOptionsSnapshot` secret-ref paths (e.g. `secret://keyvault/name`). Plus `OAuth2ClientCredentialsResolver` — fetches tokens via `client_id` + `client_secret` stored in a secret-ref.
- [ ] Policy engine integration: the JWT-extracted `AgentPrincipal` flows into `IAgentPolicyEngine.EvaluateAsync` so policies can gate per-caller.
- [ ] Observability: `Meter` for per-verb counters + latency histograms (metric names `vais.control.verb.duration` + `vais.control.verb.errors` with `verb` / `status` tags). `ActivitySource` spans around every verb ("control.agent.create", etc.) with caller principal + agent id tags. `IAuditLog` default: `LoggerAuditLog` writing to an `ILogger` with structured fields.
- [ ] Tests — 12 new: JWT validation happy + fail paths, principal populated correctly, secret-ref resolver round-trip, OAuth2 client-credentials flow (against a local fake OIDC), meter records per-verb latency, audit log captures Create/Update/Evict.
- [ ] `PublicAPI.Unshipped.txt` updates.

### PR 5 — v0.6.0-preview cut

**Packages**: all 19 (13 existing + 6 new).

Tasks:

- [ ] API freeze: `Unshipped` → `Shipped` across the 6 new packages + `Vais.Agents.Abstractions` (new `AgentLifecycleOperation` additions if any).
- [ ] Pack: `dotnet pack -c Release -p:VersionPrefix=0.6.0 -p:VersionSuffix=preview -o artifacts/packages` → 19 `.nupkg` + 19 `.snupkg`.
- [ ] Smoketest: extend with a Control-plane segment — construct one of each new type, spin up a `WebApplicationFactory` hosting the HTTP server + in-process lifecycle manager, round-trip a Create / Invoke / Signal / Evict via the typed client, YAML-load a manifest, exercise the policy engine + audit log.
- [ ] Tag: annotated `v0.6.0-preview`. Not pushed.
- [ ] Milestone log entry + research doc §7 update.

---

## Exit criteria

- [ ] All 5 PRs on OSS repo `main` (not pushed).
- [ ] 6 new packages pack cleanly at `0.6.0-preview`.
- [ ] Full non-container test suite green (expected: ~400+).
- [ ] Smoketest re-runs clean with the Control-plane segment.
- [ ] `v0.6.0-preview` tag created.
- [ ] Can do `curl -X POST http://localhost/v1/agents -d @manifest.yaml` end-to-end.

---

## Decisions locked (from the breakdown discussion 2026-04-19)

- **REST over gRPC** in MVP — deployment friction is lower; gRPC as a follow-up.
- **SSE over WebSockets** for streaming Invoke — matches LLM-provider streaming patterns, simpler infra.
- **YAML loader returns data, doesn't reconcile** — reconciliation belongs on the HTTP server (apply-style) or a future K8s operator.
- **Policy engine abstraction ships; real impl deferred** — keeps the middleware seam in place for OPA/Rego adapters to drop in.
- **JWT bearer is the only inbound auth in MVP** — covers 90% of real deployments; mTLS / API-key / SPIFFE follow up.
- **Single-cluster only** — multi-region, cross-cluster routing is its own design.
- **Manifest schema**: companion doc at [`actor-agents-oss-v0.6-manifest-schema.md`](./actor-agents-oss-v0.6-manifest-schema.md) locks the YAML/JSON wire shape — three layers (library / reasoning / control-plane), K8s-style `apiVersion`+`kind`+`metadata`+`spec`, `secret://` URI scheme for credentials.
- **Reasoning layer = contract-only in v0.6** — `agentMode` + `reasoning` fields ship on `AgentManifest`, loader round-trips them, engine ignores them (treats everything as `toolCalling`). SGR execution lands in a follow-up pillar. Cheap forward-compat, no premature execution complexity.
- **Ship both YAML and JSON loaders** — share a validation core; HTTP API uses JSON natively, CLI / file-on-disk uses YAML. `YamlAgentManifestLoader` is a YAML→JSON normaliser in front of `JsonAgentManifestLoader`.

---

## Progress log

- 2026-04-19 — plan created, MVP boundary locked, 5-PR split. **Pending**: start on PR 1 (lifecycle-manager engine + policy scaffolding) or deep-dive on the HTTP API design in a separate note before cutting any code.
- 2026-04-19 — HTTP API design sketch + manifest schema doc completed (companion docs). Decisions locked in §"Decisions locked" above.
- 2026-04-19 — **v0.6 PR 1 landed in two commits** on OSS repo `main` (not pushed). Runtime-neutral engine design: one `AgentLifecycleManager` concrete works over any `IAgentRuntime`, so `Vais.Agents.Control.Orleans` drops out of v0.6 scope (Orleans consumers wire `OrleansAgentRuntime` as their `IAgentRuntime` and use the same manager). New package count drops from 6 → 5.
  - `2774933` — **PR 1a: AgentManifest expansion + Vais.Agents.Control.Abstractions.** Library + reasoning + control-plane fields all optional init-only (no *REMOVED* churn). New contracts: `IAgentPolicyEngine` + `PolicyDecision` + `PolicyOperation` + `IAuditLog` + `AuditLogEntry` + `NullAgentPolicyEngine` + `NullAuditLog`. 16 new tests. +361 total non-container.
  - `d896ff3` — **PR 1b: AgentLifecycleManager engine** in new `Vais.Agents.Control.InProcess` package + `AgentPolicyDeniedException` in Control.Abstractions. Every verb routes through policy + audit middleware; in-flight state tracks `CancellationTokenSource` per `(AgentId, Version)` for `CancelAsync` + Active/Idle status. Signal is a policy-gated audited no-op in v0.6 (contract-only until pause-state wires into the durable journal). 13 new tests. **374 total non-container** (Core 256 +29 for PR 1 overall).
- 2026-04-19 — **v0.6 PR 2 landed** as `f5c127b` on OSS repo `main` (not pushed). Two new packages sharing a validation core:
  - `Vais.Agents.Control.Manifests.Json` — `JsonAgentManifestLoader : IAgentManifestLoader`. Full envelope + metadata + spec parsing across the library / reasoning / control-plane layers. Validation rules (id / semver / label-key / systemPrompt exactly-one / reasoning exactly-one-schema / budget positives / autoscaling min≤max / mcp command-xor-url / duplicate-id / duration formats) all enforced in a single pass with multi-error `AgentManifestValidationException`.
  - `Vais.Agents.Control.Manifests.Yaml` — `YamlAgentManifestLoader : IAgentManifestLoader`. Thin wrapper: normalises YAML (`---` multi-doc, YAML 1.2 core types, quoted-vs-unquoted scalars) to JSON via YamlDotNet's YamlStream, delegates to the JSON loader. Key-order preservation load-bearing end-to-end (YamlMappingNode preserves insertion order; normaliser emits JSON in same order; `JsonDocument` carries through). YamlDotNet 16.3.0 added to central package versions.
  - `IAgentManifestLoader` + `AgentManifestValidationException` in `Control.Abstractions`.
  - Declarative agents without explicit `spec.handler` get a synthesized `AgentHandlerRef("declarative")` sentinel — author-time ergonomics.
  - 21 new tests. **395 total non-container** (Core 277 +21). 0 warnings.
- 2026-04-19 — **v0.6 PR 3 landed** as `18f3940` on OSS repo `main` (not pushed). HTTP control plane on the wire:
  - `Vais.Agents.Control.Http.Server` — minimal-API `MapAgentControlPlane("/v1")` + `AddAgentControlPlane()` DI. Seven verb routes + health + ready. RFC 7807 Problem Details with stable `urn:vais-agents:*` type URNs (manifest-invalid / agent-not-found / policy-denied / budget-exceeded / interrupt-pending / backend-unavailable); manifest errors attach the error list as an extension so tooling gets per-field diagnostics.
  - `Vais.Agents.Control.Http.Client` — typed `IAgentControlPlaneClient` + `AgentControlPlaneClient` wrapping `HttpClient`. Internal `EnvelopeSerializer` wraps flat `AgentManifest` records into the v0.6 envelope shape on the wire (client + server see one format). Non-success HTTP surfaces as `AgentControlPlaneException(StatusCode, Type, Title, Detail)` — callers pattern-match on `Type` instead of HTTP flavour quirks.
  - New test project `Vais.Agents.Control.Http.Tests` using `Microsoft.AspNetCore.TestHost 9.0.0`. 11 end-to-end tests: healthz, full CRUD round-trip via typed client, raw JSON body via `HttpClient`, invalid-manifest Problem Details shape, policy-deny 403, client-side exception typing, version publishing via `UpdateAsync`, signal 202, cancel-vs-evict modes, missing invoke body 400.
  - **Deferred to pillar polish (post-MVP)**: SSE streaming Invoke (wire format + event taxonomy already specified in the HTTP-API design doc), OpenAPI auto-generation, `Idempotency-Key` dedupe store. Non-blocking for the v0.6 wire-shape cut.
  - **406 total non-container** (Core 277, Http.Tests 11 new). 0 warnings.
- 2026-04-19 — **v0.6 PR 4 landed** as `74cb7b4` on OSS repo `main` (not pushed). JWT auth + principal flow + audit/observability defaults close the MVP scope.
  - Control.Abstractions adds `ISecretResolver` + `SecretNotFoundException` + `IPrincipalMapper`.
  - Control.InProcess adds `LoggerAuditLog` (structured-logger audit sink with Warning-on-deny severity), `EnvironmentSecretResolver` + `FileSecretResolver` + `CompositeSecretResolver.CreateDefault()`, and `ControlPlaneDiagnostics` (`ActivitySource "Vais.Agents.Control"` + `Meter "Vais.Agents.Control"` with `vais.control.verb.{duration,count}` instruments). `AgentLifecycleManager.CreateAsync` + `InvokeAsync` wired for span + metric emission — hot-path pair, pattern demonstrated for the rest.
  - Control.Http.Server adds `DefaultPrincipalMapper` (OIDC `sub` / `tenant_id` / `scope` extraction + Azure AD `tid` fallback), `AddAgentControlPlaneJwtAuth(configure)` DI helper, and `UseAgentControlPlanePrincipalMapping()` middleware that pushes `AgentPrincipal` onto the ambient `AsyncLocalAgentContextAccessor` for the request scope. Control.Http.Server now ProjectReferences Core for the accessor. Dependency added: `Microsoft.AspNetCore.Authentication.JwtBearer 9.0.0`.
  - 16 new tests (3 LoggerAuditLog + 7 secret resolvers + 6 HTTP auth). **422 total non-container** (Core 287 +10; Http.Tests 17 +6). 0 warnings.
  - **Deferred to follow-ups**: OAuth2 client-credentials resolver, cloud-specific secret stores, full metric coverage across Signal/Query/Cancel/Update/Evict verbs.
- 2026-04-19 — **v0.6.0-preview cut landed** as `816e2b9` on OSS repo `main` + annotated tag **`v0.6.0-preview`**. Not pushed.
  - **API freeze**: one-shot `Unshipped → Shipped` across 7 packages (Abstractions + 5 new Control.* + Control.InProcess). Zero `*REMOVED*` markers — every v0.6 addition was init-only or new-type, additive over v0.5. Build clean post-freeze.
  - **Pack**: `dotnet pack -c Release -p:VersionPrefix=0.6.0 -p:VersionSuffix=preview` → **19 `.nupkg` + 19 `.snupkg`** (13 existing + 6 new control-plane packages) in `artifacts/packages/`.
  - **Smoketest** bumped to `0.6.0-preview` with a new control-plane segment: expanded `AgentManifest` construction exercising every new sub-record, `JsonAgentManifestLoader` + `YamlAgentManifestLoader` round-trip, null policy + null audit invocation, full `AgentLifecycleManager` lifecycle (Create → Invoke → Signal → Query → Cancel → Evict), `LoggerAuditLog` wiring, `CompositeSecretResolver.CreateDefault()` env lookup, `ControlPlaneDiagnostics` name probes, 9 HTTP type probes including `ProblemDetailsMapping.ManifestInvalidType` / `PolicyDeniedType` constants. Clean restore + build + run. **Finding**: `AgentListResponse` / `AgentQueryResponse` are deliberately duplicated across the server and client packages (client avoids dragging ASP.NET Core via its ref); smoketest drops both from its `typeof` array to avoid the ambiguous-type compile error — consumers referencing both packages need fully-qualified `typeof` to disambiguate.
  - **Tag**: annotated `v0.6.0-preview` on `main`. Not pushed — same discipline as v0.1 through v0.5. Public push decision deferred.
  - **All four pillar PRs closed**. Control-plane pillar complete.
