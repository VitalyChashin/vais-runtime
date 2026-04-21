# Guide: enable HTTP idempotency

Every mutating verb on the HTTP control plane — `POST /v1/agents/…/invoke`, `POST /v1/agents/…/signal`, `PUT /v1/agents/…`, `DELETE /v1/agents/…` — takes an `Idempotency-Key` header. Turn on the middleware and second attempts with the same key replay the first response instead of firing side effects twice.

Shipped in v0.11 as `AgentControlPlaneIdempotencyMiddleware` + `IIdempotencyStore` + `InMemoryIdempotencyStore` (in `Vais.Agents.Control.Http.Server`) + durable `OrleansIdempotencyStore` (in `Vais.Agents.Hosting.Orleans`). Semantics follow the Stripe shape — 24h TTL, SHA-256 body fingerprint, 4-tuple tenant-scoped keys, `Idempotency-Replayed` response header on cache hit, `422` on body mismatch, `409 + Retry-After` on an in-flight duplicate.

## Packages

```xml
<PackageReference Include="Vais.Agents.Control.Http.Server" Version="0.15.0-preview" />
<!-- Optional: durable store that survives silo restart -->
<PackageReference Include="Vais.Agents.Hosting.Orleans" Version="0.15.0-preview" />
```

## Minimal wiring — in-memory store

```csharp
using Vais.Agents.Control.Http;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddAgentControlPlane();                // v0.6 contract wiring
builder.Services.AddAgentControlPlaneIdempotency();     // v0.11 — idempotency middleware + in-memory store
builder.Services.AddAgentControlPlaneOpenApi();         // v0.11 — OpenAPI doc with URN-annotated errors

var app = builder.Build();

app.UseAuthentication();
app.UseAgentControlPlaneIdempotency();                  // middleware order matters — after auth, before MapAgentControlPlane
app.MapAgentControlPlane();
app.MapAgentControlPlaneOpenApi();                      // /openapi/v1.json

app.Run();
```

The `InMemoryIdempotencyStore` keeps entries in a `ConcurrentDictionary` with a background timer scanning for expired keys every `IdempotencyOptions.EvictionInterval` (5 minutes default). Lossy on process restart — fine for single-node dev, single-instance production, and tests. For multi-node deployments see the Orleans store below.

## Tuning `IdempotencyOptions`

```csharp
builder.Services.AddAgentControlPlaneIdempotency(options =>
{
    options.Ttl = TimeSpan.FromHours(24);               // default — matches Stripe retention
    options.EvictionInterval = TimeSpan.FromMinutes(5); // in-memory scan cadence; TimeSpan.Zero disables the timer
    options.MaxKeyLength = 255;                         // 400 Bad Request if exceeded (Stripe cap)
    options.IncludeGetsInExclusion = true;              // GET/HEAD/OPTIONS bypass the middleware (default)
    options.PathExclusions.Add("/v1/agents/*/invoke/stream");  // v0.12 SSE route bypasses by `[StreamingEndpoint]` too
});
```

Defaults are Stripe-aligned. Crank `Ttl` shorter only when your storage is tight — longer replays give callers more generous retry windows.

## Durable store — `OrleansIdempotencyStore`

`AddAgentControlPlaneIdempotency` uses `TryAddSingleton<IIdempotencyStore>`. To swap the in-memory default for Orleans grain-backed durability, register Orleans **first**:

```csharp
using Vais.Agents.Hosting.Orleans;

builder.Host.UseOrleans(silo =>
{
    silo.UseLocalhostClustering();
    silo.AddMemoryGrainStorage("Default");              // swap for Redis/Postgres in production
});

builder.Services.AddOrleansIdempotencyStore(ttl: TimeSpan.FromHours(24));   // must precede AddAgentControlPlaneIdempotency
builder.Services.AddAgentControlPlaneIdempotency();
```

Ordering note: `AddOrleansIdempotencyStore` uses `TryAddSingleton`, so it must run **before** the default wiring. The `ttl` parameter pins the grain's retention to match the middleware's `IdempotencyOptions.Ttl` — mismatched values result in the grain purging keys the middleware still considers active. Keep them equal.

## Client-side convention

The client generates a fresh key per logical operation and retries the call with the same key on transient failure:

```csharp
using Vais.Agents.Control.Http;

var client = ClientFactory.Create(config: new VaisConfigFile().FindContext("local"));

var idempotencyKey = Guid.NewGuid().ToString("N");

try
{
    var result = await client.InvokeAsync(
        agentId: "weather",
        request: new AgentInvocationRequest(Text: "What's the weather in Paris?"),
        version: null,
        idempotencyKey: idempotencyKey);
}
catch (TaskCanceledException)
{
    // Transient — retry with the same key. Server replays if the first attempt reached it.
    var result = await client.InvokeAsync(
        agentId: "weather",
        request: new AgentInvocationRequest(Text: "What's the weather in Paris?"),
        version: null,
        idempotencyKey: idempotencyKey);
}
```

Key format: opaque, up to 255 characters. UUIDs, short human-readable tags (`"refund-order-8842"`), or hashes of request metadata all work. The server does not interpret the string.

## Detecting a replay

Server emits `Idempotency-Replayed: true` on every response whose body was served from the store rather than re-executed:

```csharp
// Custom HttpMessageHandler — capture replay flag before typed client parses body.
public sealed class ReplayDetectingHandler : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        var response = await base.SendAsync(req, ct);
        if (response.Headers.TryGetValues("Idempotency-Replayed", out var values) &&
            values.Any(v => string.Equals(v, "true", StringComparison.OrdinalIgnoreCase)))
        {
            Log.Information("Idempotency replay for {Path} — server served cached response.", req.RequestUri?.PathAndQuery);
        }
        return response;
    }
}
```

Wire it into the client's underlying `HttpClient` via `AddHttpMessageHandler<ReplayDetectingHandler>()` on the named client.

## Error semantics

Three failure shapes the server returns through the idempotency middleware. All carry `ProblemDetails` with a `type` URN — wire-visible and decidable without string-parsing the message.

| Status | URN | Meaning | Client response |
|---|---|---|---|
| `400` | `urn:vais-agents:bad-request` | `Idempotency-Key` header longer than `MaxKeyLength`. | Trim the key; retry with a fresh one. |
| `409` | `urn:vais-agents:idempotency-in-flight` | Same key in use by a concurrent, unfinished request. Response includes `Retry-After: N` seconds. | Wait `Retry-After` seconds; retry with the same key. |
| `422` | `urn:vais-agents:idempotency-mismatch` | Same key with a different body fingerprint. The key was previously bound to a different payload. | Fix the retry to use the original body, or mint a new key. |

The 4-tuple that scopes a key: `(tenantId, verbRoute, canonicalPath, idempotencyKey)`. Two tenants can reuse the same key without collision; `POST /v1/agents/weather/invoke` and `POST /v1/agents/weather/signal` with the same key are independent entries; a second `PUT /v1/agents/weather` with the same key re-hits the same slot.

## What's excluded

The middleware skips the following by default:

- `GET`, `HEAD`, `OPTIONS` — naturally idempotent. Toggle with `IncludeGetsInExclusion`.
- `/healthz`, `/readyz` — liveness probes.
- v0.12 SSE route (`/v1/agents/{id}/invoke/stream`) — body is an unbounded stream; marking the endpoint with `[StreamingEndpoint]` opts it out.

Custom bypasses via `IdempotencyOptions.PathExclusions` match as path prefixes.

## Body fingerprinting

The middleware computes a SHA-256 of the raw request body (after trimming surrogate whitespace) and stores the hex digest alongside the cached response. On a replay, the incoming body is re-hashed and compared:

- Equal → serve the cached response; add `Idempotency-Replayed: true`.
- Not equal → `422 idempotency-mismatch`.

JSON serialisation order matters. If your client hashes the body before sending and wants to avoid mismatches on map-key reorderings, canonicalise the payload client-side (e.g. sort keys) before computing `Content-Length` + dispatching.

## Testing replay behaviour

The in-memory store's background timer is easy to disable for deterministic tests:

```csharp
services.AddAgentControlPlaneIdempotency(o =>
{
    o.EvictionInterval = TimeSpan.Zero;   // no background timer; entries stay until TTL scan runs manually
    o.Ttl = TimeSpan.FromSeconds(30);     // short enough to test expiry in unit tests
});
services.Replace(ServiceDescriptor.Singleton<TimeProvider>(new FakeTimeProvider()));
```

Advance the `FakeTimeProvider` past `Ttl` to verify the store purges completed entries.

## See also

- [Control plane concept](../concepts/control-plane.md) — where idempotency fits in the verb set.
- [Consume the OpenAPI spec](consume-the-openapi-spec.md) — URNs surface in the generated schema as `x-vais-type-urns` extensions.
- [Problem-details URNs reference](../reference/problem-details-urns.md) — full URN table.
- [Run on Orleans locally](run-on-orleans-locally.md) — prerequisite for the durable store.
- `samples/IdempotencyReplay` — runnable walkthrough (pending — see [samples plan](../../plans/actor-agents-oss-housekeeping-samples-plan.md)).
