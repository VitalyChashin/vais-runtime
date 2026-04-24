# v0.11 OpenAPI auto-generation + Idempotency-Key — research spike

Scoped research pass before committing to a v0.11 pillar plan. Companion to [`actor-agents-oss-extraction-research.md`](./actor-agents-oss-extraction-research.md) §7 backlog: *"OpenAPI auto-generation + `Idempotency-Key` dedupe store on the HTTP surface."* Targets the two shipped control-plane HTTP packages (`Vais.Agents.Control.Http.Server` + `Vais.Agents.Control.Http.Client`). Created 2026-04-20.

---

## Why a spike before a pillar

The two items sit at the same layer (HTTP surface polish on the v0.6 control plane) but are independent in substance. Both have small implementation surfaces, both have live design choices, and picking the wrong stack on either one is costly to reverse post-freeze. One 1-day spike + archetype exercises settles scope before we burn pillar-length time.

Spike output: findings doc + archetype consumer sketches + store-contract draft. No public surface change, no package bumps, no tag.

---

## Current state (confirmed before spike)

Verified as of 2026-04-20 (`v0.10.0-preview` on OSS `main`):

- **Server** (`Vais.Agents.Control.Http.Server/AgentControlPlaneEndpointRouteBuilderExtensions.cs:26-324`): minimal-API `MapAgentControlPlane(prefix="/v1")` mapping 7 lifecycle verbs + `/healthz` + `/readyz`. `.WithName("Agents.Create")` etc. on each route, but no `.Produces<T>()`, no `.WithSummary()`, no `.WithOpenApi()`, no OpenAPI service registration, no idempotency middleware. `ProblemDetailsMapping` maps exceptions to RFC 7807 with stable `urn:vais-agents:*` type URNs (from v0.6).
- **Client** (`Vais.Agents.Control.Http.Client/AgentControlPlaneClient.cs:26-182`): typed `HttpClient` wrapper with 8 methods (Create/List/Query/Update/Cancel/Evict/Invoke/Signal). No `Idempotency-Key` header threading; no client-side options.
- **Tests** (`Vais.Agents.Control.Http.Tests/AgentControlPlaneHttpTests.cs`): ASP.NET Core `TestServer` + `HostBuilder` integration pattern — exercises both raw `HttpClient` and typed `AgentControlPlaneClient`. 17 tests total across Http + HttpAuth test classes.
- **Write endpoints that are non-idempotent today**: `POST /v1/agents` (Create), `PATCH /v1/agents/{id}` (Update), `POST /v1/agents/{id}/invoke` (Invoke), `POST /v1/agents/{id}/signal` (Signal), `DELETE /v1/agents/{id}` (Cancel/Evict). Timeout-retry by a client can create two manifests or trigger two tool runs.

---

## Five blocking questions

1. **Q1 — Scope: combined pillar or two pillars?** OpenAPI + Idempotency both land on the same two packages, both are HTTP-surface polish, both have small impl surfaces, both sit at the v0.6 control-plane layer. Combining into one v0.11 pillar minimises API-freeze churn (two Unshipped→Shipped cycles become one) and keeps the smoketest + tag cadence aligned. Two-pillar split only makes sense if either piece turns out much bigger than scoped. Lean going in: **combined**, with PRs factored so each piece can ship standalone if the other runs long.

2. **Q2 — OpenAPI stack.** Three candidates:
   - **(a) `Microsoft.AspNetCore.OpenApi`** (.NET 9 OOB) — zero new deps; runs on the `net9.0` we already target; emits spec at `GET /openapi/{documentName}.json`; supports transformers for custom schemas. Lean-fit.
   - **(b) `Swashbuckle.AspNetCore`** — mature, battle-tested, ships Swagger UI out of the box, but adds a transitive dep. Generally on its way out of the Microsoft-blessed path (.NET 9 marketing de-emphasises it in favour of (a)).
   - **(c) `NSwag.AspNetCore`** — similar surface to (b) + built-in client codegen; heavier dep graph; codegen isn't something we want to own.

   **Decision axis**: dep footprint vs. out-of-box UI. Lean: **(a) built-in only**; consumers layer Swashbuckle's UI on top if they want Swagger UI (documented sample). Decision driver: our other packages track built-in Microsoft.Extensions.* patterns; picking (a) follows the same minimise-transitive-deps discipline.

3. **Q3 — `Idempotency-Key` semantics and shape.** The IETF draft (`draft-ietf-httpapi-idempotency-key-header`) is stable enough that all the big APIs follow one of two shapes:
   - **Stripe/Square shape** (the one everyone copies): server keys on `(tenant, method, path, key)`, stores `fingerprint(body) + full response (status + body + content-type + selected headers)` with a TTL (24h default). Replay with same key + same body ⇒ replay cached response. Replay with same key + different body ⇒ **422** `urn:vais-agents:idempotency-mismatch`. Concurrent write with in-flight key ⇒ **409** `urn:vais-agents:idempotency-in-flight`.
   - **IETF draft shape** (narrower): server MAY replay, SHOULD detect collisions, but doesn't prescribe storage or TTL. Implementation-defined.

   Scope question: which write routes get the dedupe? `Invoke` and `Create` are obvious. `Update` less so — it's rarely mass-retried, but if a client's network flakes on a version bump, you still want dedupe. `Signal` + `Cancel/Evict` are borderline — semantically idempotent-by-domain, but HTTP-level retry without dedupe still wastes a round-trip and may race. Lean: **apply to all 5 writes; GETs + health excluded**.

   Decision axis: strict mismatch semantic or permissive (always replay last response)? Lean: **strict Stripe shape — 422 on mismatch**, matches consumer expectations, catches client bugs where they reuse keys.

4. **Q4 — Store contract + impls.** New `IIdempotencyStore` abstraction in `Control.Abstractions`. Four methods needed:
   - `TryBeginAsync(key, scope, fingerprint, ct) -> IdempotencyBeginResult` — atomically reserves the key. Return one of: `New` (proceed to handler), `Replay(cached)` (fingerprint matches, return cached response), `Mismatch` (same key, different body → 422), `InFlight` (reserved but not yet completed → 409).
   - `CompleteAsync(key, scope, response, ct)` — called from middleware after the handler finishes; stores the captured response.
   - `TryGetAsync(key, scope, ct) -> CachedResponse?` — read-only fetch (useful for tests + diagnostics; otherwise subsumed by `TryBeginAsync`).
   - `EvictAsync(key, scope, ct)` — explicit eviction (post-TTL cleanup; implementations may expose this as a background timer instead).

   Two day-one impls:
   - **`InMemoryIdempotencyStore`** in `Control.Http.Server` (or possibly `Core`) — `ConcurrentDictionary<(tenant, method, path, key), Entry>` + background eviction timer with configurable TTL. Lossy on process restart; suitable for single-node dev + smoke.
   - **`OrleansIdempotencyStore`** in `Hosting.Orleans` — `IIdempotencyKeyGrain` per `(scope, key)` with `JSON-surrogate` persistence pattern (mirrors v0.8 `A2ATaskSurrogate` + v0.9 `GraphCheckpointSurrogate`). Survives silo restart.

   Decision axis: where does `IIdempotencyStore` live — `Control.Abstractions` (like `IAuditLog`) or `Control.Http.Server` (only HTTP needs it)? Lean: **`Control.Abstractions`** — it's a general dedupe contract, not HTTP-specific; future non-HTTP interop (e.g. a gRPC surface) can reuse it. A future `RedisIdempotencyStore` can drop into `Persistence.Redis` if anyone asks.

5. **Q5 — Client-side threading.** Two knobs on `AgentControlPlaneClient`:
   - **Explicit param per method.** Add an `idempotencyKey: string?` parameter to each write method. Null ⇒ no header; caller owns key generation. Ergonomic but adds to every signature.
   - **Options-level auto-generation.** New `AgentControlPlaneClientOptions.AutoGenerateIdempotencyKey = bool` (default false). When true, the client generates a GUID per write call. Matches Stripe's client library default.

   Decision axis: auto-gen by default (safe) or opt-in (predictable)? Lean: **ship both — explicit param + opt-in auto-gen**. Rationale: explicit param lets power users correlate retries; auto-gen covers the "fire-and-forget dev setup" case where callers don't want to think about retry math. Default off matches "do no harm" for existing consumers.

---

## Tasks (research + archetype exercises)

- [x] **Q1 — scope decision.** Two options sketched in findings doc: combined 4-PR pillar vs. two separate pillars. Combined wins on every axis — one API-freeze cycle vs two; same endpoint-annotations file edited by both concerns (PR 1 wires middleware, PR 3 adds OpenAPI attributes); neither blocks the other (order arbitrary); "v0.11 polishes the HTTP surface" reads cleanly as a single pillar. Standalone shippability preserved — PR 1-2 could ship as `v0.11.0-preview` alone if PR 3 (OpenAPI) has scope creep.
- [x] **Q2 — OpenAPI stack.** `Microsoft.AspNetCore.OpenApi` already reachable via existing `<FrameworkReference Include="Microsoft.AspNetCore.App" />`; zero new package refs. No polymorphic wire types cross the control plane (all types are flat records/enums), so no `IOpenApiSchemaTransformer` complexity. `IOpenApiOperationTransformer` gives us the Problem-Details URN documentation hook. Annotated-route sketch in findings §Q2.
- [x] **Q3 — Idempotency semantics.** Stripe shape chosen: 24h TTL, `(tenant, method, path, key)` scope, raw-body SHA-256 fingerprint. Cache 2xx + 4xx, release reservation on 5xx; `Idempotency-Replayed: true` header on replay; 422 Problem Details on fingerprint mismatch (`urn:vais-agents:idempotency-mismatch`); 409 + `Retry-After: 1` on in-flight (`urn:vais-agents:idempotency-in-flight`). Applied to 5 write endpoints; GETs + health excluded. Table in findings §Q3.
- [x] **Q4 — Store contract.** `IIdempotencyStore` lives in `Control.Abstractions` (3 methods: `TryBeginAsync` / `CompleteAsync` / `ReleaseAsync`; `IdempotencyBeginResult` + `IdempotencyBeginStatus` enum; `CachedResponse` record; `IdempotencyKey` record struct). `TryGetAsync` dropped — subsumed by `TryBeginAsync`'s `Replay` case. Atomicity: `ConcurrentDictionary.GetOrAdd` for InMemory; grain-state single-threading for Orleans. Same JSON-surrogate pattern as v0.8 task store + v0.9 checkpointer. Full draft in findings §Q4.
- [x] **Q5 — Middleware shape.** Position: after `UseAgentControlPlanePrincipal`, before `UseRouting`. Standard full-buffer response-capture pattern (swap `context.Response.Body` to `MemoryStream`, run `next(context)`, CopyTo original after `CompleteAsync`). Exclusion list: `GET`/`HEAD`/`OPTIONS` + `/healthz`/`/readyz`. Future SSE opt-out via `text/event-stream` content-type check. Pipeline diagram + code sketch in findings §Q4.
- [x] **Findings doc.** [`actor-agents-oss-v0.11-openapi-idempotency-findings.md`](./actor-agents-oss-v0.11-openapi-idempotency-findings.md) — Q1–Q5 synthesis + verdict (8 locked decisions + proposed 4-PR pillar shape + 2-2.5-day effort estimate).

---

## Exit criteria

- [x] All five questions answered with evidence (not opinion) — Q1 from a PR-shape sketch + rubric comparison; Q2 from `Microsoft.AspNetCore.OpenApi` capability audit + annotated-route sketch; Q3 from a response-semantics table + Stripe/IETF reconciliation; Q4 from an `IIdempotencyStore` draft + per-backend atomicity validation; Q5 from a pipeline-position diagram + response-capture code sketch.
- [x] Recommendation lands: **ready to write v0.11 pillar plan.** 8 decisions locked in the findings doc.

No public surface change. No package bumps. No tag.

---

## Progress log

- 2026-04-20 — spike plan created after design conversation. Five blocking questions scoped (pillar shape, OpenAPI stack, Idempotency semantics, store contract, middleware pipeline position). Lean positions recorded per question going in; spike is to validate or overturn each.
- 2026-04-20 — Spike complete. All five leans held up. Q1: combined 4-PR pillar wins on API-freeze-cycles + shared-file-edits + consumer framing. Q2: `Microsoft.AspNetCore.OpenApi` built-in is a clean fit — no new package refs (FrameworkReference already present), no polymorphic wire types to worry about, `IOpenApiOperationTransformer` gives us the URN-documentation hook. Q3: Stripe semantics — 24h TTL, raw-body SHA-256 fingerprint, cache 2xx+4xx, release on 5xx, 422 on mismatch, 409 on in-flight. Q4: `IIdempotencyStore` in `Control.Abstractions` with 3 methods; `InMemory` + `Orleans` impls day one (same two-tier split as v0.8/v0.9); atomicity via `ConcurrentDictionary.GetOrAdd` / grain single-threading. Q5: middleware after auth, before routing; full-buffer response capture via `MemoryStream` swap. Findings doc landed with 8 locked decisions and a proposed 4-PR pillar shape (PR 1 server idempotency + PR 2 client threading + PR 3 OpenAPI + PR 4 Orleans store + cut). Effort estimate: 2-2.5 days focused work. Zero new packages (extensions only). **Ready to write v0.11 pillar plan.**
