# v0.11.0-preview — OpenAPI auto-generation + Idempotency-Key pillar

Tactical plan for the HTTP-surface-polish pillar. Closes two related items from the [`extraction-research`](./actor-agents-oss-extraction-research.md) §7 backlog at once: *"OpenAPI auto-generation + `Idempotency-Key` dedupe store on the HTTP surface."* Grounded in the spike findings: [`actor-agents-oss-v0.11-openapi-idempotency-findings.md`](./actor-agents-oss-v0.11-openapi-idempotency-findings.md). Parallel shape to [`actor-agents-oss-v0.10-streaming-pipeline-pillar.md`](./actor-agents-oss-v0.10-streaming-pipeline-pillar.md). Created 2026-04-20.

---

## Scope

**MVP boundary locked 2026-04-20** via the research spike. Eight decisions:

1. **Combined 4-PR pillar.** Idempotency + OpenAPI ship together. Same packages, same endpoint-annotations file, same API-freeze cycle. Splitting would be wasted motion.
2. **OpenAPI stack = `Microsoft.AspNetCore.OpenApi` (.NET 9 built-in).** Zero new package refs; `<FrameworkReference Include="Microsoft.AspNetCore.App" />` already declared on `Vais.Agents.Control.Http.Server.csproj`. Spec emitted at `GET /openapi/v1.json`. Consumers who want Swagger UI layer `Swashbuckle.AspNetCore.SwaggerUI` on top themselves.
3. **Idempotency semantics = Stripe shape.** 24h default TTL; 4-tuple scope `(tenant, method, path, key)`; raw-body SHA-256 fingerprint; 2xx + 4xx responses cached; 5xx releases the reservation; `Idempotency-Replayed: true` header on replay; 422 on mismatch; 409 + `Retry-After: 1` on in-flight.
4. **`IIdempotencyStore` lives in `Control.Abstractions`.** 3 methods (`TryBeginAsync` / `CompleteAsync` / `ReleaseAsync`); `IdempotencyBeginResult` + `IdempotencyBeginStatus` enum; `CachedResponse` record; `IdempotencyKey` record struct. Non-HTTP surfaces (future gRPC / A2A) can reuse the contract.
5. **Two store impls day one.** `InMemoryIdempotencyStore` in `Control.Http.Server` (`ConcurrentDictionary` + background eviction timer) + `OrleansIdempotencyStore` in `Hosting.Orleans` (grain-per-key + JSON surrogate pattern mirroring v0.8 `A2ATaskSurrogate` / v0.9 `GraphCheckpointSurrogate`). Redis adapter deferrable to post-v0.11.
6. **Middleware position = after auth, before routing.** Scope derives from `AgentPrincipal.TenantId`; exclusion list for `GET`/`HEAD`/`OPTIONS` + `/healthz` + `/readyz`. Full-buffer response capture via `MemoryStream` swap. Future streaming (SSE) opt-out via `text/event-stream` content-type check.
7. **Client threading = explicit param + opt-in auto-gen.** Each write method on `IAgentControlPlaneClient` gains an optional `idempotencyKey: string?` parameter (default null ⇒ no header). `AgentControlPlaneClientOptions.AutoGenerateIdempotencyKey = bool` (default false) enables per-call GUID generation. Defaults preserve source-compat on every existing call site.
8. **Two new Problem-Details URNs.** `urn:vais-agents:idempotency-mismatch` (422) + `urn:vais-agents:idempotency-in-flight` (409). Added to `ProblemDetailsMapping` with factory helpers. OpenAPI operation transformer attaches them to operation responses as `x-vais-type-urns` extension for machine-readable advertisement.

### Semantic projection chosen

**HTTP control-plane polish.** Two concerns, same layer: advertise the shipped REST surface via a standards-based OpenAPI spec (so consumers can run Kiota / NSwag / Postman against it); dedupe retried writes via `Idempotency-Key`-header semantics (so retries don't double-create or double-invoke). Neither changes the URL layout; both are additive on the wire.

### Explicitly deferred to post-v0.11

- **Swagger UI / Redoc bundling.** We publish a spec; consumers layer UI.
- **Client codegen from the spec.** Consumers run Kiota / NSwag / `openapi-generator-cli` themselves.
- **`RedisIdempotencyStore`.** InMemory covers dev; Orleans covers durable. Redis when someone asks.
- **Idempotency on non-HTTP inbound surfaces.** MCP tool calls are stateless; A2A tasks carry `taskId`. Both have their own dedupe shape; layering `IIdempotencyStore` onto them is a separate pillar if desired.
- **Streaming endpoint opt-out infrastructure.** Not needed until SSE Invoke lands (separate §7 backlog item). Middleware's `text/event-stream` check is the minimal guard; no new API surface yet.
- **Response header replay beyond status + content-type.** Stripe replays a handful of safe custom headers; we don't. Consumers who need that extend `CachedResponse` in a follow-up.
- **OpenAPI versioning beyond `v1`.** `AddAgentControlPlaneOpenApi(documentName = "v1")` is parameterised; actual multi-version API evolution (v2 / v1beta / etc.) is post-v0.11.

---

## Design questions — resolved

| # | Question | Decision | Reasoning |
|---|---|---|---|
| 1 | Combined or split pillar | Combined 4-PR | Same packages + same endpoint file + one API freeze vs two |
| 2 | OpenAPI stack | Built-in `Microsoft.AspNetCore.OpenApi` | Zero new deps; FrameworkReference already present; polymorphic concern is moot (no discriminated-union types on the control-plane wire) |
| 3 | Idempotency shape | Stripe — 24h TTL / raw-body SHA-256 / 4-tuple scope / cache 2xx+4xx / release 5xx | Industry-standard; catches client bugs on mismatch; simple fingerprint |
| 4 | `IIdempotencyStore` location | `Control.Abstractions` | Non-HTTP interop surfaces can reuse; matches `IAuditLog` precedent |
| 5 | Day-one store impls | InMemory + Orleans | Same two-tier split as v0.8/v0.9 |
| 6 | Middleware position | After auth, before routing | Tenant scope available; per-path exclusion is string-match |
| 7 | Response capture | Full-buffer `MemoryStream` swap | Works for all current endpoints; streaming endpoints opt out when added |
| 8 | Client threading | Explicit param + opt-in auto-gen | Source-compat preserved; power users override; lazy users get auto-correlation |

### Open questions (low-stakes, resolve during impl)

1. **`Idempotency-Key` header length cap.** Stripe caps at 255 chars. Lean: same — reject > 255 with 400.
2. **TTL minimum.** Should we reject `IdempotencyOptions.Ttl < 1 minute`? Lean: enforce `>= 1 minute` at options validation, document as "too short defeats the point".
3. **`InMemoryIdempotencyStore` eviction cadence.** Background `Timer` firing every 5 minutes scans + evicts expired entries. Lean: tunable via `IdempotencyOptions.EvictionInterval`; default 5min.
4. **Orleans grain idle-timeout + post-TTL behaviour.** Grain deactivates naturally after idle; on reactivation, checks `CompletedAt + Ttl < now` and clears state. Lean: do the check in `ReadStateAsync`-equivalent before serving `TryBeginAsync`.
5. **OpenAPI document name default.** `"v1"` vs. empty string (which makes the URL `/openapi.json`). Lean: `"v1"` — consumers who later need `/v2.json` get a parallel document name from day one.
6. **Response-body 0-byte case.** Handler returns 204 No Content with empty body. Cache it? Lean: yes — `CachedResponse(204, "", Array.Empty<byte>(), now)` replays correctly.
7. **Idempotency + JWT auth interaction.** If a consumer rotates their token mid-retry, the idempotency layer still scopes by tenant (from the new JWT), not the caller's identity. Document as "keys are tenant-scoped, not user-scoped; same tenant + same key = dedupe, regardless of which user".

---

## No new packages

Package count stays at **22** (same as v0.9/v0.10). All v0.11 work lives as extensions inside existing packages.

Extended packages (zero breaking changes on existing surface):
- **`Vais.Agents.Control.Abstractions`** — `IIdempotencyStore` + `IdempotencyKey` + `IdempotencyBeginResult` + `IdempotencyBeginStatus` + `CachedResponse`.
- **`Vais.Agents.Control.Http.Server`** — `InMemoryIdempotencyStore` + `AgentControlPlaneIdempotencyMiddleware` + `IdempotencyOptions` + `AddAgentControlPlaneIdempotency` + `UseAgentControlPlaneIdempotency` + `AddAgentControlPlaneOpenApi` + `MapAgentControlPlaneOpenApi` + `VaisProblemDetailsOperationTransformer` + route annotations on all 7 endpoints + 2 new Problem-Details URN constants + helpers on `ProblemDetailsMapping`.
- **`Vais.Agents.Control.Http.Client`** — `AgentControlPlaneClientOptions` + per-method `idempotencyKey` parameter on 6 write methods + auto-gen.
- **`Vais.Agents.Hosting.Orleans`** — `OrleansIdempotencyStore` + `IIdempotencyKeyGrain` + `IdempotencyKeyGrain` + `IdempotencyKeyGrainState` + `IdempotencyKeySurrogate` + `AddOrleansIdempotencyStore` DI ext.

---

## Delivery

### PR 1 — Idempotency server-side (contract + InMemory + middleware)

**Packages**: `Vais.Agents.Control.Abstractions` (extend) + `Vais.Agents.Control.Http.Server` (extend).

Tasks:

- [x] `IIdempotencyStore` interface + supporting types (`IdempotencyKey` record struct, `CachedResponse` record, `IdempotencyBeginResult` record, `IdempotencyBeginStatus` enum) in `Control.Abstractions`. XML docs spell out the atomicity contract + the `TryBeginAsync` → {New, Replay, Mismatch, InFlight} dispatch shape + the `ReleaseAsync` rule (call on 5xx handler / handler exception, not on 4xx).
- [x] `InMemoryIdempotencyStore` in `Control.Http.Server` — `ConcurrentDictionary<IdempotencyKey, Entry>` with `Entry` as a private sealed record carrying `State ∈ {InFlight, Completed}` + `Fingerprint` + `CachedResponse?` + `ExpiresAt`. `TryBeginAsync` uses `GetOrAdd` with a factory returning `InFlight`; on concurrent race the second caller observes the existing `InFlight` and returns 409. Completed entries past `ExpiresAt` treated as missing (stale-detection with CAS replacement via `TryUpdate` so leftover expired entries don't pin state). Background `Timer` (fires every `IdempotencyOptions.EvictionInterval`, default 5min) scans + removes expired entries. Implements `IDisposable` for the timer. Optional `TimeProvider` ctor overload for test-time clock control.
- [x] `IdempotencyOptions` — `TimeSpan Ttl = 24h`, `TimeSpan EvictionInterval = 5min`, `int MaxKeyLength = 255`, `IList<string> PathExclusions = new()` (in addition to the built-in `/healthz` + `/readyz`), `bool IncludeGetsInExclusion = true` (documentation knob).
- [x] `AgentControlPlaneIdempotencyMiddleware` — the workhorse. Sequence on every request:
   1. Early-exit if `Method ∈ {GET, HEAD, OPTIONS}` (when `IncludeGetsInExclusion` is true) OR `Path ∈ ExclusionSet`.
   2. Early-exit if no `Idempotency-Key` header present.
   3. Validate key length ≤ `MaxKeyLength`; reject with 400 on overflow.
   4. Buffer request body (must be seekable for fingerprinting + handler consumption); compute SHA-256 fingerprint.
   5. Build `IdempotencyKey(tenant: http.User.FindFirstValue(VaisClaims.TenantId), method, path, key)`.
   6. `store.TryBeginAsync(...)`:
      - **New**: swap `context.Response.Body` to `MemoryStream`, call `next(context)`, inspect status; on `2xx`/`4xx` call `CompleteAsync`; on `5xx` call `ReleaseAsync`; copy buffer to original body + add `Idempotency-Replayed: false` header.
      - **Replay**: write cached status + content-type + body + `Idempotency-Replayed: true` header. Do not call `next`.
      - **Mismatch**: 422 Problem Details via new helper on `ProblemDetailsMapping`. Do not call `next`.
      - **InFlight**: 409 Problem Details with `Retry-After: 1`. Do not call `next`.
- [x] `AgentControlPlaneServiceCollectionExtensions.AddAgentControlPlaneIdempotency(this IServiceCollection, Action<IdempotencyOptions>?)` — registers the InMemory store via `TryAddSingleton<IIdempotencyStore, InMemoryIdempotencyStore>()` + configures options. Consumers who want Orleans call `services.AddOrleansIdempotencyStore()` (PR 4) instead, which registers the Orleans impl first — `TryAdd*` prevents double-registration.
- [x] `AgentControlPlaneIdempotencyApplicationBuilderExtensions.UseAgentControlPlaneIdempotency(this IApplicationBuilder)` — mounts the middleware. When `IIdempotencyStore` is not registered, logs a warning at startup and becomes a pass-through. XML docs recommend placement after auth / before routing.
- [x] `MapAgentControlPlane` extension unchanged: consumers wire `UseAgentControlPlaneIdempotency` explicitly in their pipeline (so they control position relative to auth).
- [x] `ProblemDetailsMapping` gains `IdempotencyMismatchType = "urn:vais-agents:idempotency-mismatch"` + `IdempotencyInFlightType = "urn:vais-agents:idempotency-in-flight"` constants + `IdempotencyMismatch(string key, string? existingFingerprint, string? instance)` + `IdempotencyInFlight(string key, TimeSpan? retryAfter, string? instance)` static helpers returning `IResult`. `RetryAfterResult` nested wrapper injects the `Retry-After` header on 409 responses.
- [x] `PublicAPI.Unshipped.txt` updates — `Control.Abstractions` +61 entries (records bring auto-synthesised `<Clone>$`, `Equals`, `GetHashCode`, operators etc. — typical Unshipped churn for record-heavy contracts), `Control.Http.Server` +22 entries.
- [x] Tests — 10 new in `Vais.Agents.Control.Http.Tests/AgentControlPlaneIdempotencyTests.cs`:
   - (1) No `Idempotency-Key` header → pass-through, no cache entry created.
   - (2) Cache miss + successful write → handler invoked once; response captured; second call with same key + same body → replays cached response (status, content-type, body match; `Idempotency-Replayed: true` header present).
   - (3) Cache miss + successful write → second call with same key + **different body** → 422 `urn:vais-agents:idempotency-mismatch`.
   - (4) Concurrent begin (simulated via manual reservation) → second call sees `InFlight`, returns 409 with `Retry-After: 1`.
   - (5) Handler throws (→ 500 Problem Details) → reservation released; retry with same key succeeds normally.
   - (6) Handler returns 404 Problem Details (4xx) → cached; retry replays the 404.
   - (7) `GET` on `/v1/agents` with an `Idempotency-Key` header → header ignored, pass-through.
   - (8) `/healthz` with `Idempotency-Key` header → pass-through.
   - (9) Tenant-scope isolation: tenant A writes with key "abc" + body X; tenant B writes with key "abc" + body Y → both succeed; neither collides.
   - (10) TTL expiry: set `Ttl = 50ms`; write + wait 100ms + replay → cache miss, new reservation.

### PR 2 — Idempotency client-side

**Packages**: `Vais.Agents.Control.Http.Client` (extend).

Tasks:

- [x] `AgentControlPlaneClientOptions` — `bool AutoGenerateIdempotencyKey = false` + `Func<string>? IdempotencyKeyFactory = null` (default: `Guid.NewGuid().ToString("N")`). Plain POCO (not `IOptions<>`-bound) because the client is a user-facing API, not a DI service by default.
- [x] `AgentControlPlaneClient` gains a second ctor `(HttpClient, AgentControlPlaneClientOptions)` for source-compat; original `(HttpClient)` preserved and delegates with `new AgentControlPlaneClientOptions()`. When `AutoGenerateIdempotencyKey = true` AND the caller doesn't pass an explicit key, client generates one via the factory.
- [x] `IAgentControlPlaneClient` gains a second overload on each write method that takes an explicit `string? idempotencyKey` — `CreateAsync(manifest, idempotencyKey, ct)` + equivalent on `UpdateAsync`, `InvokeAsync`, `SignalAsync`, `CancelAsync`, `EvictAsync`. Original overloads preserved. DIM default on each new overload delegates to the original (drops the key), so mock implementations stay source-compat. Concrete `AgentControlPlaneClient` overrides the new overloads to thread the header. **Shape adjustment**: the `cancellationToken` parameter on the new overloads is **non-optional** (no `= default`) to satisfy `RS0026` (analyzer forbids adding multiple overloads with optional parameters on a shipped symbol). Callers who want default cancellation pass `default` explicitly.
- [x] Implementation threads `idempotencyKey` (explicit or auto-generated) onto `HttpRequestMessage.Headers` via `TryAddWithoutValidation("Idempotency-Key", value)` — bypasses the well-known-header validation for what is effectively a custom header in .NET's BCL. When both explicit and auto-gen are available, explicit wins (via `?? (opts.AutoGenerate ? factory() : null)` coalescing).
- [x] `PublicAPI.Unshipped.txt` updates — `Control.Http.Client` +19 entries (new options class + 6 new class overloads + 6 new interface DIMs + 1 new ctor).
- [x] Tests — 5 new in `Vais.Agents.Control.Http.Tests/AgentControlPlaneClientIdempotencyTests.cs`:
   - (1) Explicit `idempotencyKey` forwarded on header.
   - (2) Auto-gen enabled + no explicit → header present with Guid-shaped value.
   - (3) Neither explicit nor auto-gen → no header.
   - (4) Retry with same explicit key → server replays (end-to-end through the TestServer).
   - (5) Retry with same explicit key + different body → 422 surfaces as `AgentControlPlaneException` with type URN.

### PR 3 — OpenAPI annotations + spec endpoint

**Packages**: `Vais.Agents.Control.Http.Server` (extend).

Tasks:

- [x] `AddAgentControlPlaneOpenApi(this IServiceCollection, string documentName = "v1", Action<OpenApiOptions>? configure = null)` — registers `AddOpenApi(documentName)` under the hood + wires the `VaisProblemDetailsOperationTransformer`. Also exposes `DefaultDocumentName = "v1"` public const.
- [x] `MapAgentControlPlaneOpenApi(this IEndpointRouteBuilder, string pattern = "/openapi/{documentName}.json")` — thin wrapper over `MapOpenApi(pattern)`. Consumers who want aliases (e.g. `/openapi.json`) map multiple patterns themselves.
- [x] Route annotations on all 7 endpoints in `AgentControlPlaneEndpointRouteBuilderExtensions`. Example for Create:
   ```csharp
   group.MapPost("/agents", CreateAsync)
       .WithName("Agents.Create")
       .WithSummary("Create an agent from a manifest.")
       .WithDescription("Accepts an AgentManifest as JSON or YAML. ...")
       .WithTags("Agents")
       .Accepts<AgentManifest>("application/json", "application/yaml")
       .Produces<AgentHandle>(StatusCodes.Status201Created)
       .ProducesProblem(StatusCodes.Status400BadRequest)
       .ProducesProblem(StatusCodes.Status403Forbidden)
       .ProducesProblem(StatusCodes.Status409Conflict)
       .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
   ```
   Similar annotations for Update/List/Query/Invoke/Signal/Cancel+Evict + health/ready.
- [x] `VaisProblemDetailsOperationTransformer : IOpenApiOperationTransformer` — scans each operation's response set; for `400/403/404/409/422/429/503` adds `response.Extensions["x-vais-type-urns"] = <array of applicable URN strings>` populated from a central `_urnsByStatus` map.
- [x] `PublicAPI.Unshipped.txt` updates — `Control.Http.Server` +7 entries (new transformer + new DI extensions + `DefaultDocumentName` const).
- [x] Tests — 6 new in `Vais.Agents.Control.Http.Tests/AgentControlPlaneOpenApiTests.cs`:
   - (1) `GET /openapi/v1.json` returns 200 + `application/json` + valid JSON.
   - (2) Spec lists all 7 operations by expected `operationId` (names match `.WithName(...)` values).
   - (3) Each operation has the expected summary + tags.
   - (4) `POST /agents` operation declares `AgentManifest` accept + `AgentHandle` response + `ProblemDetails` error responses.
   - (5) Error responses carry `x-vais-type-urns` extension with at least one URN.
   - (6) Spec round-trips through `System.Text.Json` cleanly (no unhandled types; no schema circular references).

### PR 4 — Orleans store + v0.11.0-preview cut

**Packages**: `Vais.Agents.Hosting.Orleans` (extend) + all 22 for the cut.

Tasks:

- [x] `IIdempotencyKeyGrain : IGrainWithStringKey` — composite grain key built by `OrleansIdempotencyStore.BuildGrainKey` (URL-encoded 4-tuple joined with `|`). Methods: `TryBeginAsync(fingerprint, ttl) -> IdempotencyGrainBeginResult`, `CompleteAsync(responseJson, ttl, completedAt)`, `ReleaseAsync()`. Post-TTL reads return `New` (state treated as missing). `IdempotencyGrainBeginResult` is the Orleans-serialisable wire type; the store translates to/from the abstraction-level `IdempotencyBeginResult`.
- [x] `IdempotencyKeyGrain : Grain, IIdempotencyKeyGrain` with `[PersistentState("idempotency-key", AiAgentGrain.StorageName)]` — reuses existing storage name, no new config. Preserves fingerprint from `TryBeginAsync` reservation across the `CompleteAsync` transition.
- [x] `IdempotencyKeyGrainState` — `HasEntry` bool + `Entry` (`IdempotencyKeySurrogate`).
- [x] `IdempotencyKeySurrogate` — flat struct with `[GenerateSerializer]` + 6 denormalised fields (`GrainKey`, `State` enum, `Fingerprint`, `ResponseJson`, `ExpiresAt`). Same JSON-blob-for-CachedResponse pattern as v0.8/v0.9.
- [x] `OrleansIdempotencyStore : IIdempotencyStore` — routes `TryBeginAsync` / `CompleteAsync` / `ReleaseAsync` through `IGrainFactory.GetGrain<IIdempotencyKeyGrain>(compositeKey)`. JSON serialization via `System.Text.Json` default options. Takes TTL via ctor (not `IOptions<>`) to avoid Orleans → Http.Server cross-package dep; `DefaultTtl` = 24h public static readonly.
- [x] `AgenticHostingOrleansServiceCollectionExtensions.AddOrleansIdempotencyStore(IServiceCollection, TimeSpan? ttl = null)` — `TryAddSingleton<IIdempotencyStore, OrleansIdempotencyStore>()`. Must be called before `AddAgentControlPlaneIdempotency` (or the InMemory default wins — documented ordering).
- [x] `PublicAPI.Unshipped.txt` updates — `Hosting.Orleans` +33 entries (grain + interface + surrogate + state + store + grain-begin-result + DI ext + `BuildGrainKey` helper + `DefaultTtl` static readonly).
- [x] Tests — 5 new in `Vais.Agents.Hosting.Orleans.Tests/OrleansIdempotencyStoreTests.cs`:
   - (1) Begin/Complete/Read replay round-trip.
   - (2) Survives grain deactivation (force collection between Complete + replay).
   - (3) TTL expiry: set 50ms TTL; write; wait 100ms; replay → New.
   - (4) Concurrent Begin → first gets `New`, second gets `InFlight`.
   - (5) Mismatch detection: Complete with fingerprint X; Begin with same key + fingerprint Y → `Mismatch`.
- [x] **API freeze**: `Unshipped` → `Shipped` on 4 packages touched by this pillar (`Control.Abstractions`, `Control.Http.Server`, `Control.Http.Client`, `Hosting.Orleans`). Other 18 packages shipped unchanged since `v0.10.0-preview`.
- [x] **Pack**: `dotnet pack Vais.Agents.sln -c Release -p:VersionPrefix=0.11.0 -p:VersionSuffix=preview -o artifacts/packages` → 22 `.nupkg` + 22 `.snupkg`.
- [x] **Smoketest**: bumped all 22 package refs to `0.11.0-preview`; added two new probe segments:
   - **Idempotency probe** (in-process, not HTTP): construct `InMemoryIdempotencyStore`; `TryBeginAsync` + `CompleteAsync` + re-`TryBeginAsync` with same fingerprint (Replay) + different fingerprint (Mismatch); assert status values + cached-body-byte round-trip. Probe line: `Idempotency: first=New replay=Replay mismatch=Mismatch cached-body-byte=42 urn-mismatch=urn:vais-agents:idempotency-mismatch urn-inflight=urn:vais-agents:idempotency-in-flight openapi-doc-default=v1 openapi-types-probed=3 orleans-idempotency-types=4`. HTTP-layer probes (middleware, spec endpoint) stay in the test suite.
   - **OpenAPI type probe**: reference `AgentControlPlaneOpenApiServiceCollectionExtensions.DefaultDocumentName` + 3 OpenAPI types (`AgentControlPlaneOpenApiServiceCollectionExtensions`, `VaisProblemDetailsOperationTransformer`, `AgentControlPlaneIdempotencyMiddleware`) + 4 Orleans idempotency types. Plus client-options probe (`AutoGenerateIdempotencyKey` default-off + opt-in + `OrleansIdempotencyStore.DefaultTtl.TotalHours = 24`).
- [x] **Tag**: annotated `v0.11.0-preview` on the API-freeze commit (`8b091c1`). Not pushed.
- [x] **Milestone log** entry in [`actor-agents-oss-milestone-log.md`](./actor-agents-oss-milestone-log.md).
- [x] **Research doc §7** update: struck through "OpenAPI auto-generation + `Idempotency-Key` dedupe store on the HTTP surface" backlog line, pointed at this pillar + findings doc.

---

## Exit criteria

- [x] All 4 PRs on OSS repo `main` (not pushed), landed as the two-commit pattern used in v0.7/v0.8/v0.9/v0.10 (feat `83d5ff4` for PRs 1-4; chore `8b091c1` for API freeze).
- [x] Zero new packages; extensions to 4 production + 1 test project pack cleanly at `0.11.0-preview` — 22 `.nupkg` + 22 `.snupkg` in `artifacts/packages/`.
- [x] Full non-container test suite green: 549 tests (523 v0.10 baseline + 10 server idempotency + 5 client idempotency + 6 OpenAPI + 5 Orleans store).
- [x] Smoketest probes both surfaces — idempotency round-trip (New/Replay/Mismatch) + OpenAPI type reachability — from a fresh .NET 9 console project with only NuGet references.
- [x] `v0.11.0-preview` tag created on the API-freeze commit.
- [ ] **Acceptance demo (manual)**: a real client retry loop (curl + shell script) against a running smoketest host shows idempotent replay; `curl http://localhost:5080/openapi/v1.json | jq .paths | wc -l` reports 7+ operations. **Not yet run** — unit-test equivalents in PR 1-4 are the automated version (10 server tests cover replay/mismatch/in-flight/5xx-release/4xx-cache/TTL/tenant-scope; 6 OpenAPI tests cover spec endpoint + URN extension + operationId enumeration). Run manually against a running host when time allows.

---

## Decisions locked (from the spike + research walkthrough 2026-04-20)

- **Combined 4-PR pillar.** One API freeze, shared endpoint-file edits, consumer-friendly framing.
- **`Microsoft.AspNetCore.OpenApi` (.NET 9 built-in).** Zero new deps; no polymorphic types to worry about; `IOpenApiOperationTransformer` for URN documentation.
- **Stripe-shape idempotency.** 24h TTL, raw-body SHA-256, 4-tuple scope, cache 2xx + 4xx, release 5xx, 422 mismatch, 409 in-flight, `Idempotency-Replayed` header.
- **`IIdempotencyStore` in `Control.Abstractions`**; InMemory (Http.Server) + Orleans (Hosting.Orleans) impls day one.
- **Middleware after auth, before routing**; exclusion list for GETs + health; full-buffer `MemoryStream` response capture.
- **Client: explicit `idempotencyKey` param + opt-in `AutoGenerateIdempotencyKey`**; defaults preserve source-compat.
- **Two new Problem-Details URNs** (`urn:vais-agents:idempotency-mismatch` + `urn:vais-agents:idempotency-in-flight`) with `ProblemDetailsMapping` factory helpers and OpenAPI-transformer attachment.
- **OpenAPI spec at `/openapi/v1.json`** via `AddAgentControlPlaneOpenApi` + `MapAgentControlPlaneOpenApi` opt-in DI+endpoint extensions; not mounted automatically by `MapAgentControlPlane`.

---

## Progress log

- 2026-04-20 — plan created after the OpenAPI + Idempotency spike closed. 8 decisions locked from the spike's verdict; 4 PRs scoped; 7 open questions flagged for impl. Package count stays at 22 (no new package). Target effort: 2-2.5 days focused work (PR 1 middleware + response capture + store + 10 tests is the bulk; PR 2 is client-side rote; PR 3 is OpenAPI annotations + transformer; PR 4 is Orleans + release cadence). **Pending**: start on PR 1 (idempotency contract + InMemory + middleware + server wiring).
- 2026-04-20 — PR 1 landed on `033-logging-improvement-read`. `Vais.Agents.Control.Abstractions` extended with 4 new public types (`IIdempotencyStore`, `IdempotencyKey` record struct, `CachedResponse` record, `IdempotencyBeginResult` record) + `IdempotencyBeginStatus` enum. `Vais.Agents.Control.Http.Server` extended with 5 new public types (`InMemoryIdempotencyStore`, `IdempotencyOptions`, `AgentControlPlaneIdempotencyMiddleware`, `AgentControlPlaneIdempotencyApplicationBuilderExtensions`, plus new `AddAgentControlPlaneIdempotency` extension on the existing `AgentControlPlaneServiceCollectionExtensions` and new `IdempotencyMismatch` / `IdempotencyInFlight` factory helpers + 2 type-URN constants on `ProblemDetailsMapping`). 10 new tests in `Vais.Agents.Control.Http.Tests/AgentControlPlaneIdempotencyTests.cs` exercising no-header pass-through, matching-body replay, mismatched-body 422, in-flight 409 with `Retry-After`, 5xx release (via `ThrowingRegistry` that exposes a throwing `Register(AgentManifest)` that the lifecycle manager invokes via reflection), 4xx caching + replay (valid-JSON manifest-invalid body ⇒ AgentManifestValidationException ⇒ 400), GET-with-header pass-through, `/healthz`-with-header pass-through, tenant-scope isolation, TTL expiry (via `Microsoft.Extensions.TimeProvider.Testing`). Full non-container suite green: 533 across the whole solution (523 after v0.10 + 10 new idempotency). `Microsoft.Extensions.TimeProvider.Testing 10.1.0` added to CPM + Control.Http.Tests. **Shape adjustments during impl**: (1) `InMemoryIdempotencyStore.TryBeginAsync` uses a reserve-or-observe loop: `GetOrAdd` for the fast CAS path, then stale-entry detection with `TryUpdate` replacement so an expired leftover doesn't look like live InFlight/Replay to the caller; (2) the middleware captures `HttpResponse.Body` via `MemoryStream` swap even on exception paths, releasing the reservation in a `catch` block before rethrowing — downstream exception handlers then emit their own response on the ORIGINAL body stream (we restore it pre-throw); (3) open-question #4 resolved: Orleans grain idle-timeout check happens in `TryBeginAsync` itself (not via post-TTL deactivation) — matches the InMemory store's approach where stale entries are replaced in-place; (4) open-question #7 resolved via the tenant-scope-isolation test — confirmed that two different tenants with the same key + same path don't collide. **Pending**: PR 2 (client-side threading — `AgentControlPlaneClientOptions` + per-method `idempotencyKey` parameter + auto-gen).
- 2026-04-20 — PR 2 landed on `033-logging-improvement-read`. `Vais.Agents.Control.Http.Client` extended with `AgentControlPlaneClientOptions` (public class: `AutoGenerateIdempotencyKey` + `IdempotencyKeyFactory`). `IAgentControlPlaneClient` gains 6 new DIM overloads (one per write method) accepting `string? idempotencyKey`; DIM default delegates to the original + drops the key so mock implementations don't break. `AgentControlPlaneClient` adds a second ctor accepting `AgentControlPlaneClientOptions`, overrides the 6 new overloads to thread the `Idempotency-Key` header via `TryAddWithoutValidation` (auto-gen via factory when opted-in + explicit key absent). `InvokeAsync`/`SignalAsync` migrated from `PostAsJsonAsync` helper to explicit `HttpRequestMessage` construction so the idempotency header rides along (the helper has no header-config hook). 5 new tests in `AgentControlPlaneClientIdempotencyTests.cs` — explicit forwarding, auto-gen when opted in, no-header default, end-to-end replay (two POSTs + same key + same body ⇒ registry ends with exactly one manifest, proving server-side dedupe), mismatch surfacing as `AgentControlPlaneException` with the 422 status + type URN. Full non-container suite green: 538 across the whole solution (533 after PR 1, +5). Control.Http.Tests: 27 → 32. **Shape adjustment from original plan**: all 6 new overloads have **non-optional** `cancellationToken` (no `= default`) to satisfy PublicAPI analyzer rule `RS0026` ("Do not add multiple overloads with optional parameters"). Callers who want default cancellation pass `default` explicitly — small ergonomic cost, no behavioural change. **Pending**: PR 3 (OpenAPI annotations + spec endpoint + URN operation transformer).
- 2026-04-20 — PR 3 landed on `033-logging-improvement-read`. `Vais.Agents.Control.Http.Server` extended with 2 new public types (`VaisProblemDetailsOperationTransformer`, `AgentControlPlaneOpenApiServiceCollectionExtensions`) + `DefaultDocumentName = "v1"` const + `AddAgentControlPlaneOpenApi` DI ext + `MapAgentControlPlaneOpenApi` endpoint ext. `AgentControlPlaneEndpointRouteBuilderExtensions` gained `.WithSummary` / `.WithDescription` / `.WithTags` / `.Accepts<T>` / `.Produces<T>` / `.ProducesProblem` annotations on all 7 control-plane routes + health/ready. `Microsoft.AspNetCore.OpenApi 9.0.11` added to CPM + Http.Server csproj. 6 new tests in `AgentControlPlaneOpenApiTests.cs` — spec endpoint 200 + JSON content-type, all 7 operationIds present, summaries + tags attached, Create operation accepts AgentManifest + responds with AgentHandle 201, error responses carry `x-vais-type-urns` extension (422 → IdempotencyMismatchType; 409 → IdempotencyInFlightType; 403 → PolicyDeniedType), spec round-trips cleanly through System.Text.Json. Full non-container suite green: 544 across the whole solution (538 after PR 2 + 6 new). Control.Http.Tests: 32 → 38. **Shape adjustments during impl**: (1) URN map lives as a `_urnsByStatus` `IReadOnlyDictionary<string, string[]>` in `VaisProblemDetailsOperationTransformer` — single source of truth for status→URN resolution, easy to extend when new URNs land; (2) the `/openapi/v1.json` endpoint is opt-in via `MapAgentControlPlaneOpenApi` rather than auto-mounted by `MapAgentControlPlane` — matches the "consumers control their pipeline explicitly" stance taken for `UseAgentControlPlaneIdempotency` in PR 1; (3) the `.WithTags("Agents")` call on the `MapGroup` sets the default tag for all routes beneath it; health/ready override with `.WithTags("Health")` so the spec's Swagger-UI-compatible tags section stays clean. **Pending**: PR 4 (OrleansIdempotencyStore + v0.11.0-preview cut — API freeze, pack 22 packages, smoketest extension, tag).
- 2026-04-20 — PR 4 landed on OSS `main`. Two commits: `83d5ff4 feat(http): OpenAPI + Idempotency-Key pillar (v0.11 PRs 1-4)` (33 files, +2580 −23) + `8b091c1 chore: API freeze for v0.11.0-preview — promote Unshipped -> Shipped` (8 files, Unshipped→Shipped promotion across 4 packages — Abstractions +60 entries, Http.Server +29, Http.Client +19, Hosting.Orleans +33). Annotated `v0.11.0-preview` tag created on `8b091c1` (not pushed). 22 `.nupkg` + 22 `.snupkg` packed at `0.11.0-preview` into `artifacts/packages/`. Smoketest refreshed to `0.11.0-preview` with idempotency + OpenAPI probes; ran clean. Probe line: `Idempotency: first=New replay=Replay mismatch=Mismatch cached-body-byte=42 urn-mismatch=urn:vais-agents:idempotency-mismatch urn-inflight=urn:vais-agents:idempotency-in-flight openapi-doc-default=v1 openapi-types-probed=3 orleans-idempotency-types=4`. Final line: `"All twenty-two Vais.Agents.* 0.11.0-preview packages consumed cleanly from a plain .NET 9 console app."` Milestone log entry appended (`actor-agents-oss-milestone-log.md`). Research doc §7 "OpenAPI auto-generation + `Idempotency-Key` dedupe store" backlog line struck through and pointed at this pillar + findings doc. **Pillar closed.** **Shape adjustments during impl**: (1) Orleans grain returns `IdempotencyGrainBeginResult` (Orleans-serialisable wire type in Hosting.Orleans) rather than `IdempotencyBeginResult` directly — Control.Abstractions has no Orleans attributes, so directly returning the abstraction record failed `OrleansConfigurationException` on grain-startup type check. `OrleansIdempotencyStore` translates between the two. Same pattern as v0.8's A2A surrogate. (2) Hosting.Orleans gained a first-time ProjectReference to Control.Abstractions (previously only `Abstractions` + `Core`) — `IIdempotencyStore` is a neutral dedupe contract that belongs there. (3) `OrleansIdempotencyStore` takes TTL via ctor (not `IOptions<IdempotencyOptions>`) to avoid Orleans → Http.Server cross-package dep for a single TimeSpan; consumers who want HTTP-TTL propagation pass it explicitly via `AddOrleansIdempotencyStore(ttl: ...)`. (4) `ORLEANS0014` re-encountered — all `.ConfigureAwait(false)` removed from grain code (known from v0.9 checkpointer). (5) Only follow-up remaining: the manual acceptance demo (curl + retry loop against a running smoketest host) — unit-test equivalents are green.
