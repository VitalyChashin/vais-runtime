# OpenApiSpecExplorer

Inspect the shipped OpenAPI spec + its `x-vais-type-urns` extension. Boots a `WebApplication` with `AddAgentControlPlaneOpenApi()`, fetches `GET /openapi/v1.json`, and prints every path and every error-response URN annotation the `VaisProblemDetailsOperationTransformer` injects.

## Run

```bash
dotnet run --project samples/OpenApiSpecExplorer
```

## Expected output

```
Server: http://127.0.0.1:<port>

OpenAPI 3.0.1  —  OpenApiSpecExplorer | v1

== paths ==
  /v1/agents
  /v1/agents/{id}
  /v1/agents/{id}/invoke
  /v1/agents/{id}/invoke/stream
  /v1/agents/{id}/signal
  /v1/healthz
  /v1/readyz

== x-vais-type-urns annotations ==
  POST /v1/agents  [400]  urn:vais-agents:manifest-invalid, urn:vais-agents:bad-request
  POST /v1/agents  [403]  urn:vais-agents:policy-denied
  POST /v1/agents  [409]  urn:vais-agents:interrupt-pending, urn:vais-agents:idempotency-in-flight
  POST /v1/agents  [422]  urn:vais-agents:idempotency-mismatch
  POST /v1/agents  [503]  urn:vais-agents:backend-unavailable
  ...
  POST /v1/agents/{id}/invoke/stream  [501]  urn:vais-agents:streaming-not-supported
  ...

Done.
```

*(Full output shows all 7 paths × their error status codes — ~33 URN annotations total.)*

## What it demonstrates

- `AddAgentControlPlaneOpenApi()` — registers the .NET 10 `Microsoft.AspNetCore.OpenApi` document generator + the `VaisProblemDetailsOperationTransformer` that enriches each error response with `x-vais-type-urns`.
- `MapAgentControlPlaneOpenApi()` — mounts `GET /openapi/{documentName}.json`; default document name is `v1` → `/openapi/v1.json`.
- `x-vais-type-urns` extension — array of stable `urn:vais-agents:*` type URNs on every 4xx/5xx response. Client codegen can pattern-match on these URNs at runtime without parsing strings from the Problem Details body.
- `System.Text.Json.JsonDocument` — no OpenAPI client library needed; parse the spec as plain JSON to print paths and extensions.

## Docs

- [OpenAPI spec](../../docs/guides/openapi-spec.md)
- [`HttpIdempotencyInMemory`](../HttpIdempotencyInMemory) — companion v0.11 sample (idempotency middleware)
