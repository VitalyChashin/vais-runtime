# v0.11 OpenAPI + Idempotency — spike findings

Synthesis of the research spike scoped in [`actor-agents-oss-v0.11-openapi-idempotency-spike.md`](./actor-agents-oss-v0.11-openapi-idempotency-spike.md). Answers Q1–Q5 with evidence, not opinion. Landing verdict at the bottom.

Created 2026-04-20. **Status**: complete. All five questions resolved from code audit + IETF draft + Stripe/Square behaviour + .NET 9 OpenAPI capability audit.

---

## Q1 — Pillar shape

### Option sketch — combined 4-PR pillar

| PR | Packages | Net content | LOC est |
|----|---|---|---|
| PR 1 | `Control.Abstractions` + `Control.Http.Server` | `IIdempotencyStore` contract + `InMemoryIdempotencyStore` + middleware (`UseAgentControlPlaneIdempotency`) + option registration + server wiring into `MapAgentControlPlane` | 350-450 |
| PR 2 | `Control.Http.Client` | Client threading: `AgentControlPlaneClientOptions` + per-method `idempotencyKey` parameter + optional auto-gen | 80-150 |
| PR 3 | `Control.Http.Server` | OpenAPI annotations on 7 routes + `AddAgentControlPlaneOpenApi` DI extension + operation-transformer for Problem Details type-URN documentation | 200-300 |
| PR 4 | `Hosting.Orleans` + cut | `OrleansIdempotencyStore` + grain + surrogate + `AddOrleansIdempotencyStore` DI ext; API freeze; pack 22 packages at `0.11.0-preview`; smoketest probes; tag | 250-350 + release scaffolding |

### Option sketch — two separate pillars

- Pillar A (v0.11): idempotency only, 2 PRs (server+client, then Orleans+cut). Package count: unchanged (22).
- Pillar B (v0.12): OpenAPI only, 2 PRs (annotations + spec endpoint, then cut). Package count: unchanged (22).

### Decision (Q1): **combined 4-PR pillar**

Combined wins on every axis:
- **API freeze cycles: 1 vs 2.** Each freeze is ~30 minutes of PublicAPI promotion + pack + smoketest + tag. Doubling it for no gain is waste.
- **Same endpoint-annotations file is touched by both.** PR 1 edits `AgentControlPlaneEndpointRouteBuilderExtensions.cs` to wire middleware; PR 3 edits the same file to add `.WithOpenApi(...)` / `.Produces<T>()` calls. Split across pillars means two rebases or two refactors.
- **Neither blocks the other.** Order is arbitrary between PR 1/2 (idempotency) and PR 3 (OpenAPI). PR 4 (Orleans + cut) is the terminal step.
- **Ergonomic framing for consumers.** "v0.11 polishes the HTTP surface" reads cleanly. "v0.11 is idempotency, v0.12 is OpenAPI" reads as tax.

Standalone shippability preserved: PR 1-2 (idempotency) could ship as `v0.11.0-preview` alone with PR 3 (OpenAPI) bumped to v0.12 if scope creep on PR 3 emerges. PR factoring makes this cheap.

---

## Q2 — OpenAPI stack

### Capability audit — `Microsoft.AspNetCore.OpenApi` 9.0

- **Distribution**: part of `Microsoft.AspNetCore.App` shared framework on .NET 9. Already reachable via `<FrameworkReference Include="Microsoft.AspNetCore.App" />` which `Vais.Agents.Control.Http.Server.csproj` already declares. **Zero new package refs.**
- **Registration**: `builder.Services.AddOpenApi(documentName: "v1")` registers the generator. `app.MapOpenApi()` maps `GET /openapi/{documentName}.json` (so `GET /openapi/v1.json` by default).
- **Route annotations on minimal-API**: `.WithSummary(string)`, `.WithDescription(string)`, `.WithTags(string[])`, `.Produces<T>(statusCode, contentType?)`, `.ProducesProblem(statusCode, contentType?)`, `.WithOpenApi(op => { ... })` for finer-grained operation metadata.
- **Schema emission**: reflects over DTOs. Our control-plane wire types are simple records with primitive/record fields + `JsonElement` holes for opaque payloads. `JsonElement` renders as `object` in the spec, which is correct — opaque is opaque.
- **Polymorphic types**: .NET 9 `AddOpenApi` honours `[JsonPolymorphic]` + `[JsonDerivedType]` attributes, emitting `oneOf` schemas with discriminators. **Not needed for v0.11** — no polymorphic types cross the control-plane wire (verified: `AgentManifest`, `AgentHandle`, `AgentInvocationRequest/Result`, `AgentSignal`, `AgentStatus`, `AgentListResponse`, `AgentQueryResponse` are all flat records/enums).
- **Transformers**: `IOpenApiDocumentTransformer`, `IOpenApiOperationTransformer`, `IOpenApiSchemaTransformer` hooks let us post-process the generated document. We'd use `IOpenApiOperationTransformer` to enrich each `ProblemDetails` response with an `x-vais-type-urns` extension listing the applicable `urn:vais-agents:*` values — machine-readable way to advertise stable error types without adding per-status schemas.

### Annotated-route sketch

```csharp
group.MapPost("/agents", CreateAsync)
    .WithName("Agents.Create")
    .WithSummary("Create an agent from a manifest.")
    .WithDescription(
        "Accepts an AgentManifest as JSON or YAML (Content-Type: application/json or application/yaml). " +
        "The Idempotency-Key header is honoured — retries with the same key + same body replay the " +
        "original response; different body → 422 with urn:vais-agents:idempotency-mismatch.")
    .WithTags("Agents")
    .Accepts<AgentManifest>("application/json", "application/yaml")
    .Produces<AgentHandle>(StatusCodes.Status201Created)
    .ProducesProblem(StatusCodes.Status400BadRequest)
    .ProducesProblem(StatusCodes.Status403Forbidden)
    .ProducesProblem(StatusCodes.Status409Conflict)
    .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
```

Operation transformer attaches the URN list per status code:

```csharp
// Registered via services.AddOpenApi("v1", o => o.AddOperationTransformer<VaisProblemDetailsAugmenter>());
internal sealed class VaisProblemDetailsAugmenter : IOpenApiOperationTransformer
{
    public Task TransformAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken ct)
    {
        foreach (var (status, response) in operation.Responses)
        {
            if (status == "400" || status == "403" || status == "409" || status == "422")
            {
                response.Extensions["x-vais-type-urns"] = new OpenApiArray
                {
                    new OpenApiString(ProblemDetailsMapping.UrnsForStatus(int.Parse(status)))
                    // ... concrete URNs per status
                };
            }
        }
        return Task.CompletedTask;
    }
}
```

### Decision (Q2): **`Microsoft.AspNetCore.OpenApi` (built-in), no Swashbuckle/NSwag**

Zero new deps; runs on the .NET 9 we already target; polymorphic concern is moot; operation transformer gives us the URN-documentation hook we need. Consumers who want Swagger UI (the interactive HTML viewer) add `Swashbuckle.AspNetCore.SwaggerUI` themselves — we publish the spec; they pick their viewer.

---

## Q3 — Idempotency-Key semantics

### Shape — follow Stripe, which follows the IETF draft

| Scenario | Server behaviour | HTTP response |
|---|---|---|
| No `Idempotency-Key` header on write | Pass through to handler (no dedupe) | Handler's normal response |
| Key + matching fingerprint (replay) | Serve cached response | Original status + body + content-type; adds `Idempotency-Replayed: true` header |
| Key + different fingerprint (mismatch) | Reject | **422** Problem Details, `type=urn:vais-agents:idempotency-mismatch`, `detail="Idempotency-Key '{key}' already used with a different body."` |
| Key currently in-flight (another concurrent request) | Reject | **409** Problem Details, `type=urn:vais-agents:idempotency-in-flight`, `Retry-After: 1` header, `detail="Another request with the same key is in progress."` |
| Key + cache miss (new or TTL-expired) | Reserve + proceed | Handler's normal response; middleware captures + stores on success |
| Handler fails (5xx) with reserved key | Release reservation; do NOT cache failure | Handler's normal response; next retry gets a fresh slot |

### Cache scope

`(tenant_id, method, path, key)` 4-tuple. `tenant_id` from `AgentPrincipal.TenantId` (already populated by v0.6 JWT auth middleware). Anonymous clients share a `null` tenant scope — documented limitation (use unique keys or authenticate).

### Fingerprint

**Raw body hash** (SHA-256 of the UTF-8 body bytes). Matches Stripe; simple; strict. Consumers who worry about whitespace sensitivity can canonicalise before sending. We don't.

### TTL

**24h default** (matches Stripe), configurable via `IdempotencyOptions.Ttl`. Cleaner than 1h (too short for overnight jobs) and 7d (ties up memory).

### Routes covered

All 5 write endpoints:
- `POST /v1/agents` (Create)
- `PATCH /v1/agents/{id}` (Update)
- `POST /v1/agents/{id}/invoke` (Invoke) — most impact
- `POST /v1/agents/{id}/signal` (Signal)
- `DELETE /v1/agents/{id}` (Cancel/Evict) — domain-idempotent but still benefits from HTTP-level dedupe

GETs + `/healthz` + `/readyz`: excluded (naturally idempotent).

### Failed-handler semantics

Handler exception (including 5xx Problem Details from `ProblemDetailsMapping`): **do not cache**. The reservation is released, next retry can proceed. Rationale: transient-error 5xx is the whole point of retries. Caching a 500 would wedge the client for 24h.

Handler-returned 4xx with valid response body (not our throw-then-map path — a deliberate 4xx like 404): **cache**. A "not found" response is a real answer; retrying won't change it. This matches Stripe — their docs say "responses are cached regardless of status".

Simplification for v0.11: we only cache 2xx + handler-returned 4xx. 5xx (including mapped Problem Details from exceptions) releases the reservation. Easy rule, consumer-safe.

### Decision (Q3): **Stripe shape, 24h TTL, 4-tuple scope, raw-body SHA-256 fingerprint, cache 2xx + 4xx only, release reservation on 5xx**

---

## Q4 — Store contract

### `IIdempotencyStore` in `Control.Abstractions`

```csharp
namespace Vais.Agents.Control;

public interface IIdempotencyStore
{
    ValueTask<IdempotencyBeginResult> TryBeginAsync(
        IdempotencyKey key,
        string fingerprint,
        CancellationToken cancellationToken);

    ValueTask CompleteAsync(
        IdempotencyKey key,
        CachedResponse response,
        CancellationToken cancellationToken);

    ValueTask ReleaseAsync(
        IdempotencyKey key,
        CancellationToken cancellationToken);
}

public readonly record struct IdempotencyKey(
    string? TenantId,
    string Method,
    string Path,
    string Key);

public sealed record CachedResponse(
    int StatusCode,
    string ContentType,
    byte[] Body,
    DateTimeOffset CompletedAt);

public enum IdempotencyBeginStatus { New, Replay, Mismatch, InFlight }

public sealed record IdempotencyBeginResult(
    IdempotencyBeginStatus Status,
    CachedResponse? CachedResponse = null,
    string? ExistingFingerprint = null);
```

**Three methods** (trimmed from the spike's draft — `TryGetAsync` subsumed by `TryBeginAsync`'s `Replay` case). `ReleaseAsync` is called on 5xx / handler exception to free the reservation.

### Atomicity validation

- **`InMemoryIdempotencyStore`**: `ConcurrentDictionary<IdempotencyKey, Entry>` where `Entry` is either `InFlight(fingerprint, SemaphoreSlim wait?)` or `Completed(fingerprint, CachedResponse, expiresAt)`. `TryBeginAsync` uses `GetOrAdd` with a factory that returns `InFlight` — CAS semantics on the dictionary give us atomicity. Concurrent racing attempts: first wins with `New`, subsequent see `InFlight` and return 409. Background timer evicts entries past `expiresAt`.
- **`OrleansIdempotencyStore`**: `IIdempotencyKeyGrain` per `(tenant, method, path, key)` string (composite key); grains are single-threaded, so `TryBeginAsync` is a straightforward grain-state read/write with no concurrency worry. Orleans persistence layer handles durability; grain deactivation respects TTL via a grain timer that self-deactivates post-expiry. Same JSON-surrogate pattern as v0.8 `A2ATaskSurrogate` + v0.9 `GraphCheckpointSurrogate`.

### Response capture mechanics

```csharp
// pseudocode inside UseAgentControlPlaneIdempotency
if (beginResult.Status == IdempotencyBeginStatus.New)
{
    var originalBody = context.Response.Body;
    using var buffer = new MemoryStream();
    context.Response.Body = buffer;
    try
    {
        await next(context);
        buffer.Position = 0;
        var bodyBytes = buffer.ToArray();

        if (context.Response.StatusCode < 500)
        {
            var cached = new CachedResponse(
                context.Response.StatusCode,
                context.Response.ContentType ?? "application/octet-stream",
                bodyBytes,
                DateTimeOffset.UtcNow);
            await store.CompleteAsync(idempotencyKey, cached, context.RequestAborted);
        }
        else
        {
            await store.ReleaseAsync(idempotencyKey, context.RequestAborted);
        }

        // Stream buffered body downstream
        await originalBody.WriteAsync(bodyBytes, context.RequestAborted);
    }
    finally { context.Response.Body = originalBody; }
}
```

Full-buffer capture is fine for our control plane (no streaming endpoints). Future streaming endpoints (SSE Invoke, listed as a separate deferred item in §7) would opt out via a convention — middleware checks `context.Features.Get<IHttpResponseBodyFeature>()` for a marker, or checks `Content-Type: text/event-stream` on the outgoing response (and skips `CompleteAsync`).

### Middleware pipeline position

```
Request
  ↓
UseAuthentication (existing — JWT → ClaimsPrincipal)
  ↓
UseAgentControlPlanePrincipal (existing v0.6 — populates AgentPrincipal)
  ↓
UseAgentControlPlaneIdempotency (NEW — scope derives from AgentPrincipal.TenantId)
  ↓
UseRouting
  ↓
UseAuthorization
  ↓
MapAgentControlPlane endpoints
```

**After auth** (so tenant scope is known) **before routing** (we scope by path string, not route data). Exclusion list baked in: skip if `Method ∈ {GET, HEAD, OPTIONS}` OR `Path ∈ {prefix/healthz, prefix/readyz}`.

### Decision (Q4): **`IIdempotencyStore` in `Control.Abstractions` — 3 methods, `IdempotencyBeginResult` status enum, `CachedResponse` record, `IdempotencyKey` record struct. `InMemoryIdempotencyStore` in `Control.Http.Server`; `OrleansIdempotencyStore` in `Hosting.Orleans`.**

Non-HTTP interop surfaces (future gRPC, A2A) can reuse the contract.

---

## Q5 — Client-side threading

### Shape

```csharp
namespace Vais.Agents.Control.Http;

public sealed class AgentControlPlaneClientOptions
{
    /// <summary>
    /// When true, the client generates a fresh Guid for each write call that doesn't
    /// already carry an explicit idempotencyKey. Default false — callers own their keys.
    /// </summary>
    public bool AutoGenerateIdempotencyKey { get; init; }

    /// <summary>Factory used when AutoGenerateIdempotencyKey is true. Default: Guid.NewGuid().ToString("N").</summary>
    public Func<string>? IdempotencyKeyFactory { get; init; }
}

public interface IAgentControlPlaneClient
{
    // existing methods gain an optional idempotencyKey parameter:
    Task<AgentHandle> CreateAsync(AgentManifest manifest, string? idempotencyKey = null, CancellationToken cancellationToken = default);
    Task<AgentHandle> UpdateAsync(string agentId, AgentManifest newManifest, string? version = null, string? idempotencyKey = null, CancellationToken cancellationToken = default);
    Task<AgentInvocationResult> InvokeAsync(string agentId, AgentInvocationRequest request, string? version = null, string? idempotencyKey = null, CancellationToken cancellationToken = default);
    Task SignalAsync(string agentId, AgentSignal signal, string? version = null, string? idempotencyKey = null, CancellationToken cancellationToken = default);
    Task CancelAsync(string agentId, string? version = null, string? idempotencyKey = null, CancellationToken cancellationToken = default);
    Task EvictAsync(string agentId, string? version = null, string? idempotencyKey = null, CancellationToken cancellationToken = default);

    // GETs unchanged.
}
```

- Default values on new parameters preserve source-compat for every existing call site.
- Adding optional parameters to interface methods is additive in PublicAPI terms (new overload-compatible shapes); keeps Shipped.txt churn minimal.
- `AgentControlPlaneClient` ctor takes optional `AgentControlPlaneClientOptions` (default `new()` instance — no auto-gen).

### Retry / correlation

Explicit-param wins when caller retries and wants the server to dedupe (caller threads the same key on all retries of one logical op). Auto-gen wins when caller doesn't retry but wants to prevent duplicate submission from a misbehaving middleware or proxy.

### Decision (Q5): **both — explicit `idempotencyKey` parameter on each write method + opt-in auto-gen via `AgentControlPlaneClientOptions.AutoGenerateIdempotencyKey`. Defaults preserve source-compat.**

---

## Verdict — ready to write the pillar plan

### Locked decisions

1. **Combined 4-PR pillar** (idempotency first, then OpenAPI, Orleans + cut last).
2. **OpenAPI stack = `Microsoft.AspNetCore.OpenApi` (.NET 9 built-in).** Zero new package refs; `<FrameworkReference Include="Microsoft.AspNetCore.App" />` already declared. Operation transformer for Problem Details URN documentation.
3. **Idempotency semantics = Stripe shape.** 24h default TTL; 4-tuple scope `(tenant, method, path, key)`; raw-body SHA-256 fingerprint; 2xx + 4xx cached, 5xx releases reservation; `Idempotency-Replayed: true` header on replay; 422 on mismatch; 409 + `Retry-After: 1` on in-flight.
4. **`IIdempotencyStore` in `Control.Abstractions`.** 3 methods (`TryBeginAsync` / `CompleteAsync` / `ReleaseAsync`); `IdempotencyBeginResult` status enum; `CachedResponse` record; `IdempotencyKey` record struct.
5. **Two store impls day one: `InMemoryIdempotencyStore` (Control.Http.Server) + `OrleansIdempotencyStore` (Hosting.Orleans).** Same two-tier split as v0.8 task store + v0.9 checkpointer.
6. **Middleware position: after auth, before routing.** Full-buffer response capture; exclusion list for GETs + health + future streaming endpoints.
7. **Client threading: explicit `idempotencyKey` parameter + opt-in auto-gen via options.** Defaults preserve source-compat.
8. **Two new problem-type URNs: `urn:vais-agents:idempotency-mismatch` (422) + `urn:vais-agents:idempotency-in-flight` (409).** Added to `ProblemDetailsMapping`.

### Proposed PR shape (4 PRs)

**PR 1 — Idempotency server-side.**
- `IIdempotencyStore` + `IdempotencyKey` + `CachedResponse` + `IdempotencyBeginResult` + `IdempotencyBeginStatus` in `Control.Abstractions`.
- `InMemoryIdempotencyStore` in `Control.Http.Server` (ConcurrentDictionary + background eviction timer).
- `IdempotencyOptions` (TTL, auto-generate-on-missing, exclusion list overrides) + `AddAgentControlPlaneIdempotency` DI extension.
- `AgentControlPlaneIdempotencyMiddleware` + `UseAgentControlPlaneIdempotency` app builder extension.
- `MapAgentControlPlane` wires `UseAgentControlPlaneIdempotency` automatically when the service is registered (TryAddSingleton discipline — absent ⇒ middleware skipped).
- `ProblemDetailsMapping` gains two URNs + two response helpers (`IdempotencyMismatch(key)` / `IdempotencyInFlight(key, retryAfter)`).
- Tests: ~10 in `Vais.Agents.Control.Http.Tests/AgentControlPlaneIdempotencyTests.cs` — cache miss (pass-through + cache), cache hit matching fingerprint (replay), cache hit mismatched fingerprint (422), in-flight concurrent (409), 5xx handler (release, no cache), 4xx handler (cache), GET excluded, no-header passthrough, tenant-scope isolation, TTL expiry.
- PublicAPI: Abstractions +~15 entries, Http.Server +~10 entries.

**PR 2 — Idempotency client-side.**
- `AgentControlPlaneClientOptions` in `Control.Http.Client`.
- `IAgentControlPlaneClient` + `AgentControlPlaneClient` gain optional `idempotencyKey: string?` parameter on the 6 write methods.
- Auto-gen when options opt in.
- `Idempotency-Key` header threading.
- Tests: ~5 in `Vais.Agents.Control.Http.Tests/AgentControlPlaneClientIdempotencyTests.cs` — explicit key forwarded, auto-gen when opted in, no header when not, retry with same key replays, retry with different body 422.
- PublicAPI: Http.Client +~8 entries.

**PR 3 — OpenAPI.**
- `AddAgentControlPlaneOpenApi(documentName = "v1")` DI extension in `Control.Http.Server`.
- `MapAgentControlPlaneOpenApi(pattern = "/openapi/{documentName}.json")` endpoint extension.
- Route annotations on all 7 endpoints (`.WithSummary` / `.WithDescription` / `.WithTags` / `.Produces<T>` / `.ProducesProblem`).
- `VaisProblemDetailsOperationTransformer` decorating error responses with `x-vais-type-urns` extension.
- Tests: ~6 in `Vais.Agents.Control.Http.Tests/AgentControlPlaneOpenApiTests.cs` — spec endpoint responds 200 with JSON; all 7 routes present; `Produces` schemas resolve; Problem Details responses documented; URN extension populated; contract-test asserting the JSON matches a snapshot (or that the shape round-trips through a JSON-schema validator).
- PublicAPI: Http.Server +~6 entries.

**PR 4 — Orleans store + v0.11.0-preview cut.**
- `OrleansIdempotencyStore` + `IIdempotencyKeyGrain` + `IdempotencyKeyGrainState` + `IdempotencyKeySurrogate` + `AddOrleansIdempotencyStore` DI ext in `Hosting.Orleans`.
- Tests: ~5 in `Vais.Agents.Hosting.Orleans.Tests/OrleansIdempotencyStoreTests.cs` — Begin/Complete/Release round-trip; survives grain deactivation; TTL eviction; concurrent begins; mismatch detection.
- API freeze: `Unshipped` → `Shipped` across `Control.Abstractions`, `Control.Http.Server`, `Control.Http.Client`, `Hosting.Orleans` (4 packages).
- Pack 22 packages at `0.11.0-preview` into `artifacts/packages/`.
- Smoketest: bump to `0.11.0-preview`; add idempotency probe (register `InMemoryIdempotencyStore`, send two POSTs with same key, assert replay) + OpenAPI probe (fetch `/openapi/v1.json`, assert 7 operations present).
- Annotated `v0.11.0-preview` tag on the API-freeze commit.
- Milestone log entry. Research doc §7 backlog line struck through.

### Effort estimate

4 PRs, each ~1 focused session. Largest is PR 1 (middleware + response capture + store + 10 tests). Smallest is PR 2 (client threading is rote once the server side is done). Budget 2-2.5 days of focused work — slightly larger than v0.10 (retry boundary + adapter tests) because response-capture middleware is fiddly and Orleans grain adds real work.

### Non-goals for v0.11

- **Swagger UI / Redoc bundling.** We emit a spec; consumers layer UI themselves.
- **OpenAPI client codegen.** Out of scope; consumers run their own Kiota / NSwag / `openapi-generator-cli` against the spec.
- **Redis `IdempotencyStore`.** Not required; can land in `Persistence.Redis` if anyone asks.
- **Idempotency for non-HTTP surfaces.** A2A-inbound + MCP-inbound have their own semantics (MCP tool calls are stateless, A2A tasks have `taskId`). Out of scope.
- **Streaming endpoint opt-out infra.** Not needed until SSE Invoke lands (separate §7 backlog item).
- **SDK/bindings-level compat (generated .NET client via Kiota).** Out of scope.

---

## Open items (for pillar planning, not blockers)

- **TTL default naming.** `IdempotencyOptions.Ttl` vs `.CacheDuration` vs `.EntryLifetime`. Lean: `Ttl` — matches Stripe's docs, industry-standard abbreviation.
- **Configurable auto-response-capture content-type list.** What if a consumer returns `application/octet-stream`? Lean: capture regardless; consumer owns the rule that idempotent endpoints return deterministic bodies.
- **Response header capture.** Do we replay all headers or only a subset (status, content-type)? Stripe replays a handful of safe headers (Request-Id, etc.). Lean: content-type + status only; custom headers aren't cached. Simpler; consumers who need more can extend `CachedResponse`.
- **Orleans grain storage provider name.** Reuse `AiAgentGrain.StorageName` (as v0.8 task store + v0.9 checkpointer do) or register a new one? Lean: reuse. No new config for consumers.
- **Key-size limit.** Max length on `Idempotency-Key` header value. Stripe caps at 255 chars. Lean: same — reject > 255 with 400.
- **OpenAPI document name.** Just "v1"? Or expose as option? Lean: `AddAgentControlPlaneOpenApi(documentName = "v1")` — parameterised, default stays "v1".
